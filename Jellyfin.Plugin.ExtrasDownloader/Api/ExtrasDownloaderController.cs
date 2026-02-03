using System.Net.Mime;
using Jellyfin.Data.Enums;
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
    private readonly ILogger<ExtrasDownloaderController> _logger;

    public ExtrasDownloaderController(
        ILibraryManager libraryManager,
        ExtrasDownloadQueue downloadQueue,
        ILogger<ExtrasDownloaderController> logger)
    {
        _libraryManager = libraryManager;
        _downloadQueue = downloadQueue;
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

#endregion
