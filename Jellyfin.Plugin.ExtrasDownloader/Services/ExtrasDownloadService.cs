using Jellyfin.Plugin.ExtrasDownloader.Downloaders;
using Jellyfin.Plugin.ExtrasDownloader.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Services;

/// <summary>
/// Background service that processes the extras download queue.
/// </summary>
public class ExtrasDownloadService : BackgroundService
{
    private readonly ExtrasDownloadQueue _queue;
    private readonly TmdbVideoProvider _tmdbProvider;
    private readonly YtDlpDownloader _downloader;
    private readonly PotServerManager _potServer;
    private readonly ILogger<ExtrasDownloadService> _logger;

    public ExtrasDownloadService(
        ExtrasDownloadQueue queue,
        TmdbVideoProvider tmdbProvider,
        YtDlpDownloader downloader,
        PotServerManager potServer,
        ILogger<ExtrasDownloadService> logger)
    {
        _queue = queue;
        _tmdbProvider = tmdbProvider;
        _downloader = downloader;
        _potServer = potServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Extras download service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var request) && request != null)
                {
                    await ProcessRequestAsync(request, stoppingToken);
                }
                else
                {
                    // No items in queue, wait before checking again
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in extras download service loop");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        // Cleanup
        _potServer.StopManagedServer();
        _logger.LogInformation("Extras download service stopped");
    }

    private async Task ProcessRequestAsync(ExtrasDownloadRequest request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available");
            return;
        }

        _logger.LogInformation("Processing extras for: {Name} (TMDB: {TmdbId})",
            request.ItemName, request.TmdbId);

        try
        {
            // Ensure POT server is running (if configured or bundled)
            var potAvailable = await _potServer.EnsureRunningAsync(cancellationToken);
            if (!potAvailable)
            {
                _logger.LogWarning(
                    "POT server not available. YouTube downloads may fail with bot detection. " +
                    "Consider setting up bgutil-ytdlp-pot-provider.");
            }

            // Check if yt-dlp is available
            if (!await _downloader.IsAvailableAsync())
            {
                _logger.LogError("yt-dlp is not available");
                return;
            }

            // Fetch videos from TMDB
            var videos = request.ItemType == ItemType.Movie
                ? await _tmdbProvider.GetMovieVideosAsync(request.TmdbId, cancellationToken: cancellationToken)
                : await _tmdbProvider.GetTvShowVideosAsync(request.TmdbId, cancellationToken: cancellationToken);

            if (videos.Count == 0)
            {
                _logger.LogDebug("No videos found for {Name}", request.ItemName);
                _queue.MarkProcessed(request.ItemId);
                return;
            }

            // Filter based on configuration
            var filtered = _tmdbProvider.FilterVideos(videos);
            _logger.LogInformation("Found {Count} videos to download for {Name}", filtered.Count, request.ItemName);

            if (filtered.Count == 0)
            {
                _queue.MarkProcessed(request.ItemId);
                return;
            }

            // Check for existing extras
            if (config.SkipExistingExtras && HasExistingExtras(request.ItemPath))
            {
                _logger.LogDebug("Skipping {Name} - already has extras", request.ItemName);
                _queue.MarkProcessed(request.ItemId);
                return;
            }

            if (config.OnlyMissingTrailers && HasExistingTrailer(request.ItemPath))
            {
                _logger.LogDebug("Skipping {Name} - already has trailer", request.ItemName);
                _queue.MarkProcessed(request.ItemId);
                return;
            }

            // Download each video
            var downloadCount = 0;
            foreach (var video in filtered)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Determine output directory
                var outputDir = config.OrganizeIntoFolders
                    ? Path.Combine(request.ItemPath, video.SubfolderName)
                    : request.ItemPath;

                Directory.CreateDirectory(outputDir);

                // Check if already downloaded
                if (IsAlreadyDownloaded(outputDir, video.Name, video.JellyfinSuffix))
                {
                    _logger.LogDebug("Already downloaded: {VideoName}", video.Name);
                    continue;
                }

                var result = await _downloader.DownloadAsync(video, outputDir, null, cancellationToken);
                if (result != null)
                {
                    downloadCount++;
                    _logger.LogInformation("Downloaded: {VideoName} -> {Path}", video.Name, result);

                    // Rate limiting between videos
                    if (config.SleepBetweenVideos > 0)
                    {
                        _logger.LogDebug("Sleeping {Seconds}s before next video", config.SleepBetweenVideos);
                        await Task.Delay(TimeSpan.FromSeconds(config.SleepBetweenVideos), cancellationToken);
                    }
                }
            }

            _logger.LogInformation("Completed {Name}: {Count} videos downloaded", request.ItemName, downloadCount);
            _queue.MarkProcessed(request.ItemId);

            // Rate limiting between items
            if (downloadCount > 0 && config.SleepBetweenItems > 0)
            {
                _logger.LogDebug("Sleeping {Seconds}s before next item", config.SleepBetweenItems);
                await Task.Delay(TimeSpan.FromSeconds(config.SleepBetweenItems), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process extras for {Name}", request.ItemName);
        }
    }

    private static bool HasExistingExtras(string itemPath)
    {
        if (!Directory.Exists(itemPath))
            return false;

        var videoExtensions = new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov" };
        var extrasFolders = new[] { "Trailers", "Featurettes", "Behind The Scenes", "Scenes", "Deleted Scenes", "Shorts", "Interviews" };

        foreach (var folder in extrasFolders)
        {
            var folderPath = Path.Combine(itemPath, folder);
            if (Directory.Exists(folderPath))
            {
                if (Directory.EnumerateFiles(folderPath)
                    .Any(f => videoExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasExistingTrailer(string itemPath)
    {
        if (!Directory.Exists(itemPath))
            return false;

        var videoExtensions = new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov" };

        // Check Trailers subfolder
        var trailersDir = Path.Combine(itemPath, "Trailers");
        if (Directory.Exists(trailersDir))
        {
            if (Directory.EnumerateFiles(trailersDir)
                .Any(f => videoExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        // Check for Kodi-style trailer
        return Directory.EnumerateFiles(itemPath)
            .Any(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase) &&
                       videoExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static bool IsAlreadyDownloaded(string directory, string videoName, string suffix)
    {
        if (!Directory.Exists(directory))
            return false;

        var safeName = new string(videoName.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
        return Directory.GetFiles(directory)
            .Any(f =>
            {
                var fileName = Path.GetFileNameWithoutExtension(f);
                return fileName.Contains(safeName, StringComparison.OrdinalIgnoreCase) ||
                       fileName.Contains(suffix, StringComparison.OrdinalIgnoreCase);
            });
    }
}
