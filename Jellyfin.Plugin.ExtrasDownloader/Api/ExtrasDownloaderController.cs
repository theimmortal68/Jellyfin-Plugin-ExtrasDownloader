using System.Net.Mime;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExtrasDownloader.Downloaders;
using Jellyfin.Plugin.ExtrasDownloader.Providers;
using Jellyfin.Plugin.ExtrasDownloader.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Api;

/// <summary>
/// API controller for Extras Downloader plugin.
/// </summary>
[ApiController]
[Route("ExtrasDownloader")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class ExtrasDownloaderController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ExtrasDownloadQueue _downloadQueue;
    private readonly YtDlpDownloader _ytDlpDownloader;
    private readonly TmdbVideoProvider _tmdbVideoProvider;
    private readonly ILogger<ExtrasDownloaderController> _logger;

    public ExtrasDownloaderController(
        ILibraryManager libraryManager,
        ExtrasDownloadQueue downloadQueue,
        YtDlpDownloader ytDlpDownloader,
        TmdbVideoProvider tmdbVideoProvider,
        ILogger<ExtrasDownloaderController> logger)
    {
        _libraryManager = libraryManager;
        _downloadQueue = downloadQueue;
        _ytDlpDownloader = ytDlpDownloader;
        _tmdbVideoProvider = tmdbVideoProvider;
        _logger = logger;
    }

    /// <summary>
    /// Trigger extras download for a specific item.
    /// </summary>
    /// <param name="itemId">The item ID (GUID).</param>
    /// <returns>Status of the request.</returns>
    [HttpPost("Download/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadResponse> DownloadExtras([FromRoute] Guid itemId)
    {
        _logger.LogInformation("Manual extras download requested for item: {ItemId}", itemId);

        // Look up the item
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            _logger.LogWarning("Item not found: {ItemId}", itemId);
            return NotFound(new DownloadResponse { Success = false, Message = "Item not found" });
        }

        // Validate item type
        if (item is not Movie && item is not Series)
        {
            _logger.LogWarning("Item {ItemId} is not a Movie or Series: {Type}", itemId, item.GetType().Name);
            return BadRequest(new DownloadResponse
            {
                Success = false,
                Message = $"Item must be a Movie or Series, got {item.GetType().Name}"
            });
        }

        // Get TMDB ID
        if (!item.TryGetProviderId("Tmdb", out var tmdbIdStr) || !int.TryParse(tmdbIdStr, out var tmdbId))
        {
            _logger.LogWarning("Item {Name} has no TMDB ID", item.Name);
            return BadRequest(new DownloadResponse
            {
                Success = false,
                Message = "Item does not have a TMDB ID. Please refresh metadata first."
            });
        }

        // Determine item type
        var itemType = item switch
        {
            Movie => ItemType.Movie,
            Series => ItemType.Series,
            _ => ItemType.Movie
        };

        // Queue with high priority and force flag
        var request = new ExtrasDownloadRequest
        {
            ItemId = item.Id,
            ItemName = item.Name,
            TmdbId = tmdbId,
            ItemType = itemType,
            ItemPath = item.ContainingFolderPath,
            Priority = DownloadPriority.High
        };

        _downloadQueue.EnqueueForced(request);

        _logger.LogInformation("Queued {Name} (TMDB: {TmdbId}) for extras download", item.Name, tmdbId);

        return Ok(new DownloadResponse
        {
            Success = true,
            Message = $"Queued '{item.Name}' for extras download",
            ItemName = item.Name,
            TmdbId = tmdbId
        });
    }

    /// <summary>
    /// Get the current queue status.
    /// </summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        return Ok(new StatusResponse
        {
            HasPendingItems = _downloadQueue.HasPendingItems,
            QueueCount = _downloadQueue.Count
        });
    }

    /// <summary>
    /// Get a direct stream URL for a YouTube video.
    /// </summary>
    /// <param name="videoKey">The YouTube video key (e.g., "dQw4w9WgXcQ").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The direct stream URL.</returns>
    [HttpGet("StreamUrl")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StreamUrlResponse>> GetStreamUrl(
        [FromQuery] string videoKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoKey))
        {
            return BadRequest(new StreamUrlResponse
            {
                StreamUrl = null,
                ExtractedAt = DateTime.UtcNow,
                Error = "videoKey parameter is required"
            });
        }

        _logger.LogInformation("Stream URL requested for video key: {VideoKey}", videoKey);

        var youtubeUrl = $"https://www.youtube.com/watch?v={videoKey}";
        var streamUrl = await _ytDlpDownloader.GetStreamUrlAsync(youtubeUrl, cancellationToken);

        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("Failed to extract stream URL for video key: {VideoKey}", videoKey);
            return BadRequest(new StreamUrlResponse
            {
                StreamUrl = null,
                ExtractedAt = DateTime.UtcNow,
                Error = "Failed to extract stream URL"
            });
        }

        return Ok(new StreamUrlResponse
        {
            StreamUrl = streamUrl,
            ExtractedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get available videos for a movie or TV show from TMDB.
    /// </summary>
    /// <param name="tmdbId">The TMDB ID of the movie or TV show.</param>
    /// <param name="type">The item type: "movie" or "tv".</param>
    /// <param name="extraType">Optional filter by video type: Trailer, Teaser, Featurette, etc.</param>
    /// <param name="officialOnly">If true, only return official videos.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available videos with type information.</returns>
    [HttpGet("Videos/{tmdbId:int}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VideosResponse>> GetVideos(
        [FromRoute] int tmdbId,
        [FromQuery] string type = "movie",
        [FromQuery] string? extraType = null,
        [FromQuery] bool officialOnly = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Videos requested for TMDB {TmdbId} ({Type}), extraType={ExtraType}, officialOnly={OfficialOnly}",
            tmdbId, type, extraType, officialOnly);

        IReadOnlyList<TmdbVideo> videos;

        if (type.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            videos = await _tmdbVideoProvider.GetTvShowVideosAsync(tmdbId, cancellationToken: cancellationToken);
        }
        else
        {
            videos = await _tmdbVideoProvider.GetMovieVideosAsync(tmdbId, cancellationToken: cancellationToken);
        }

        // Filter by extra type if specified
        IEnumerable<TmdbVideo> filtered = videos;

        if (!string.IsNullOrEmpty(extraType))
        {
            filtered = filtered.Where(v => v.Type.Equals(extraType, StringComparison.OrdinalIgnoreCase));
        }

        if (officialOnly)
        {
            filtered = filtered.Where(v => v.Official);
        }

        // Only include YouTube videos (supported by yt-dlp)
        filtered = filtered.Where(v => v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase));

        // Order: official first, then by published date (newest first)
        var result = filtered
            .OrderByDescending(v => v.Official)
            .ThenByDescending(v => v.PublishedAt)
            .Select(v => new VideoInfo
            {
                Key = v.Key,
                Name = v.Name,
                Type = v.Type,
                Official = v.Official,
                PublishedAt = v.PublishedAt,
                Language = v.Language,
                Size = v.Size
            })
            .ToList();

        _logger.LogInformation("Returning {Count} videos for TMDB {TmdbId}", result.Count, tmdbId);

        return Ok(new VideosResponse
        {
            TmdbId = tmdbId,
            Videos = result
        });
    }
}

#region Response Models

public class DownloadResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public int? TmdbId { get; set; }
}

public class StatusResponse
{
    public bool HasPendingItems { get; set; }
    public int QueueCount { get; set; }
}

public class StreamUrlResponse
{
    [JsonPropertyName("streamUrl")]
    public string? StreamUrl { get; set; }

    [JsonPropertyName("extractedAt")]
    public DateTime ExtractedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class VideosResponse
{
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }

    [JsonPropertyName("videos")]
    public List<VideoInfo> Videos { get; set; } = new();
}

public class VideoInfo
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("official")]
    public bool Official { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }
}

#endregion
