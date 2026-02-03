using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Services;

/// <summary>
/// Monitors the Jellyfin library for new items and queues them for extras download.
/// Implements IServerEntryPoint to start with the server and subscribe to library events.
/// </summary>
public class LibraryMonitor : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryMonitor> _logger;
    private readonly ExtrasDownloadQueue _downloadQueue;
    private bool _disposed;

    public LibraryMonitor(
        ILibraryManager libraryManager,
        ILogger<LibraryMonitor> logger,
        ExtrasDownloadQueue downloadQueue)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _downloadQueue = downloadQueue;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available, library monitoring disabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting Extras Downloader library monitor");

        // Subscribe to library events
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Extras Downloader library monitor");

        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;

        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        ProcessItemChange(e.Item, "added");
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        // Only process metadata refreshes, not playback state changes
        if (e.UpdateReason == ItemUpdateType.MetadataDownload ||
            e.UpdateReason == ItemUpdateType.MetadataImport)
        {
            ProcessItemChange(e.Item, "updated");
        }
    }

    private void ProcessItemChange(BaseItem item, string action)
    {
        // Only process movies and series
        if (item is not (Movie or Series))
        {
            return;
        }

        // Check if plugin is enabled
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrEmpty(config.TmdbApiKey))
        {
            return;
        }

        // Check if item has TMDB ID
        if (!item.TryGetProviderId("Tmdb", out var tmdbId) || string.IsNullOrEmpty(tmdbId))
        {
            _logger.LogDebug("Item {Name} has no TMDB ID, skipping", item.Name);
            return;
        }

        _logger.LogInformation("Item {Action}: {Name} (TMDB: {TmdbId})", action, item.Name, tmdbId);

        // Queue for download
        _downloadQueue.Enqueue(new ExtrasDownloadRequest
        {
            ItemId = item.Id,
            ItemName = item.Name,
            TmdbId = int.Parse(tmdbId),
            ItemType = item is Movie ? ItemType.Movie : ItemType.Series,
            ItemPath = item.ContainingFolderPath,
            Priority = action == "added" ? DownloadPriority.High : DownloadPriority.Normal
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
    }
}

/// <summary>
/// Queue for extras download requests.
/// </summary>
public class ExtrasDownloadQueue
{
    private readonly ConcurrentQueue<ExtrasDownloadRequest> _highPriorityQueue = new();
    private readonly ConcurrentQueue<ExtrasDownloadRequest> _normalPriorityQueue = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _processedItems = new();
    private readonly ILogger<ExtrasDownloadQueue> _logger;

    public ExtrasDownloadQueue(ILogger<ExtrasDownloadQueue> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets whether there are items in the queue.
    /// </summary>
    public bool HasPendingItems => !_highPriorityQueue.IsEmpty || !_normalPriorityQueue.IsEmpty;

    /// <summary>
    /// Gets the total number of items in the queue.
    /// </summary>
    public int Count => _highPriorityQueue.Count + _normalPriorityQueue.Count;

    /// <summary>
    /// Enqueues an item for extras download, bypassing the "recently processed" check.
    /// Used for manual/forced downloads.
    /// </summary>
    public void EnqueueForced(ExtrasDownloadRequest request)
    {
        // Remove from processed items so it will be processed again
        _processedItems.TryRemove(request.ItemId, out _);

        var queue = request.Priority == DownloadPriority.High ? _highPriorityQueue : _normalPriorityQueue;
        queue.Enqueue(request);

        _logger.LogInformation("Force-queued {Name} for extras download (priority: {Priority})",
            request.ItemName, request.Priority);
    }

    /// <summary>
    /// Enqueues an item for extras download.
    /// </summary>
    public void Enqueue(ExtrasDownloadRequest request)
    {
        // Check if recently processed (within retention period)
        var config = Plugin.Instance?.Configuration;
        var retentionDays = config?.ProcessedItemRetentionDays ?? 7;

        if (_processedItems.TryGetValue(request.ItemId, out var processedTime))
        {
            if (DateTime.UtcNow - processedTime < TimeSpan.FromDays(retentionDays))
            {
                _logger.LogDebug("Item {Name} was recently processed, skipping", request.ItemName);
                return;
            }
        }

        var queue = request.Priority == DownloadPriority.High ? _highPriorityQueue : _normalPriorityQueue;
        queue.Enqueue(request);

        _logger.LogDebug("Queued {Name} for extras download (priority: {Priority})",
            request.ItemName, request.Priority);
    }

    /// <summary>
    /// Tries to dequeue the next item for processing.
    /// </summary>
    public bool TryDequeue(out ExtrasDownloadRequest? request)
    {
        // High priority first
        if (_highPriorityQueue.TryDequeue(out request))
        {
            return true;
        }

        return _normalPriorityQueue.TryDequeue(out request);
    }

    /// <summary>
    /// Marks an item as processed.
    /// </summary>
    public void MarkProcessed(Guid itemId)
    {
        _processedItems[itemId] = DateTime.UtcNow;
        CleanupOldEntries();
    }

    private void CleanupOldEntries()
    {
        var config = Plugin.Instance?.Configuration;
        var retentionDays = config?.ProcessedItemRetentionDays ?? 7;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);

        var oldKeys = _processedItems
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldKeys)
        {
            _processedItems.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// Request to download extras for an item.
/// </summary>
public class ExtrasDownloadRequest
{
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int TmdbId { get; set; }
    public ItemType ItemType { get; set; }
    public string ItemPath { get; set; } = string.Empty;
    public DownloadPriority Priority { get; set; }
}

public enum ItemType
{
    Movie,
    Series
}

public enum DownloadPriority
{
    Normal,
    High
}
