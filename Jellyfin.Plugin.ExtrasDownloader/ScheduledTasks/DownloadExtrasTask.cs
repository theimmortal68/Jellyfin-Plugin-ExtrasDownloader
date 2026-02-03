using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExtrasDownloader.Downloaders;
using Jellyfin.Plugin.ExtrasDownloader.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.ScheduledTasks;

/// <summary>
/// Scheduled task that downloads extras for library items.
/// </summary>
public class DownloadExtrasTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly TmdbVideoProvider _tmdbProvider;
    private readonly YtDlpDownloader _downloader;
    private readonly ILogger<DownloadExtrasTask> _logger;

    public DownloadExtrasTask(
        ILibraryManager libraryManager,
        TmdbVideoProvider tmdbProvider,
        YtDlpDownloader downloader,
        ILogger<DownloadExtrasTask> logger)
    {
        _libraryManager = libraryManager;
        _tmdbProvider = tmdbProvider;
        _downloader = downloader;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Download Extras";

    /// <inheritdoc />
    public string Description => "Downloads trailers and extras from TMDB/YouTube for movies and TV shows.";

    /// <inheritdoc />
    public string Category => "Extras Downloader";

    /// <inheritdoc />
    public string Key => "ExtrasDownloaderTask";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run daily at 3 AM
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Plugin not configured");
            return;
        }

        if (string.IsNullOrEmpty(config.TmdbApiKey))
        {
            _logger.LogError("TMDB API key not configured");
            return;
        }

        // Check if yt-dlp is available
        if (!await _downloader.IsAvailableAsync())
        {
            _logger.LogError("yt-dlp is not available. Please install it or configure the correct path.");
            return;
        }

        // Get all movies and TV shows
        var items = GetLibraryItems();
        _logger.LogInformation("Found {Count} items to process", items.Count);

        var processed = 0;
        var downloaded = 0;
        var failed = 0;

        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var result = await ProcessItemAsync(item, cancellationToken);
                downloaded += result;

                // Sleep between items only if downloads occurred (rate limiting)
                if (result > 0 && config.SleepBetweenItems > 0)
                {
                    _logger.LogDebug("Sleeping {Seconds}s before next item", config.SleepBetweenItems);
                    await Task.Delay(TimeSpan.FromSeconds(config.SleepBetweenItems), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process {ItemName}", item.Name);
                failed++;
            }

            processed++;
            progress.Report((double)processed / items.Count);
        }

        _logger.LogInformation(
            "Extras download complete. Processed: {Processed}, Downloaded: {Downloaded}, Failed: {Failed}",
            processed, downloaded, failed);
    }

    private IReadOnlyList<BaseItem> GetLibraryItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true
        };

        return _libraryManager.GetItemList(query);
    }

    private async Task<int> ProcessItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        // Get TMDB ID from provider IDs
        if (!item.TryGetProviderId("Tmdb", out var tmdbIdStr) ||
            !int.TryParse(tmdbIdStr, out var tmdbId))
        {
            _logger.LogDebug("No TMDB ID for {ItemName}, skipping", item.Name);
            return 0;
        }

        var config = Plugin.Instance!.Configuration;

        // Get the extras directory
        var extrasDir = GetExtrasDirectory(item, config.OrganizeIntoFolders);
        if (extrasDir == null)
        {
            _logger.LogWarning("Could not determine extras directory for {ItemName}", item.Name);
            return 0;
        }

        // Check if we should skip items with existing extras
        if (config.SkipExistingExtras && HasExistingExtras(extrasDir))
        {
            _logger.LogDebug("Skipping {ItemName} - already has extras", item.Name);
            return 0;
        }

        // Check if we should only process items missing trailers
        if (config.OnlyMissingTrailers && HasExistingTrailer(extrasDir))
        {
            _logger.LogDebug("Skipping {ItemName} - already has trailer (OnlyMissingTrailers=true)", item.Name);
            return 0;
        }

        // Fetch videos from TMDB
        var videos = item switch
        {
            Movie => await _tmdbProvider.GetMovieVideosAsync(tmdbId, cancellationToken: cancellationToken),
            Series => await _tmdbProvider.GetTvShowVideosAsync(tmdbId, cancellationToken: cancellationToken),
            _ => Array.Empty<TmdbVideo>()
        };

        if (videos.Count == 0)
        {
            _logger.LogDebug("No videos found for {ItemName}", item.Name);
            return 0;
        }

        // Filter based on configuration
        var filtered = _tmdbProvider.FilterVideos(videos);
        _logger.LogInformation("Found {Count} videos to download for {ItemName}", filtered.Count, item.Name);

        var downloadCount = 0;

        foreach (var video in filtered)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Determine output directory (organized by type or flat)
            var outputDir = config.OrganizeIntoFolders
                ? Path.Combine(extrasDir, video.SubfolderName)
                : extrasDir;

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

                // Sleep between videos (rate limiting to avoid YouTube blocks)
                if (config.SleepBetweenVideos > 0)
                {
                    _logger.LogDebug("Sleeping {Seconds}s before next video", config.SleepBetweenVideos);
                    await Task.Delay(TimeSpan.FromSeconds(config.SleepBetweenVideos), cancellationToken);
                }
            }
        }

        return downloadCount;
    }

    private static string? GetExtrasDirectory(BaseItem item, bool organized)
    {
        // For movies: same directory as the movie file
        // For series: the series root directory
        var path = item.ContainingFolderPath;
        if (string.IsNullOrEmpty(path))
            return null;

        if (organized)
        {
            // Extras will go into subfolders like "trailers/", "behind the scenes/"
            return path;
        }
        else
        {
            // All extras go into a single "extras" folder
            return Path.Combine(path, "extras");
        }
    }

    private static bool HasExistingExtras(string extrasDir)
    {
        if (!Directory.Exists(extrasDir))
            return false;

        // Check for video files in extras directory or subdirectories
        var videoExtensions = new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov" };
        return Directory.EnumerateFiles(extrasDir, "*", SearchOption.AllDirectories)
            .Any(f => videoExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasExistingTrailer(string extrasDir)
    {
        if (!Directory.Exists(extrasDir))
            return false;

        var videoExtensions = new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov" };

        // Check for Trailers subfolder
        var trailersDir = Path.Combine(extrasDir, "Trailers");
        if (Directory.Exists(trailersDir))
        {
            if (Directory.EnumerateFiles(trailersDir)
                .Any(f => videoExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        // Check for Kodi-style trailer in same directory (e.g., "moviename-trailer.mkv")
        return Directory.EnumerateFiles(extrasDir)
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

        var safeName = SanitizeForComparison(videoName);
        return Directory.GetFiles(directory)
            .Any(f =>
            {
                var fileName = Path.GetFileNameWithoutExtension(f);
                return fileName.Contains(safeName, StringComparison.OrdinalIgnoreCase) ||
                       fileName.Contains(suffix, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string SanitizeForComparison(string name)
    {
        return new string(name.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
    }
}
