using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.ExtrasDownloader.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Providers;

/// <summary>
/// Fetches video metadata from TMDB.
/// </summary>
public class TmdbVideoProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TmdbVideoProvider> _logger;
    private const string BaseUrl = "https://api.themoviedb.org/3";

    public TmdbVideoProvider(HttpClient httpClient, ILogger<TmdbVideoProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Gets videos for a movie from TMDB.
    /// </summary>
    public async Task<IReadOnlyList<TmdbVideo>> GetMovieVideosAsync(int tmdbId, string? language = null, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (string.IsNullOrEmpty(config?.TmdbApiKey))
        {
            _logger.LogWarning("TMDB API key not configured");
            return Array.Empty<TmdbVideo>();
        }

        // Extract first language from comma-separated list (e.g., "en-US,de-DE" -> "en-US")
        var lang = language ?? config.PreferredLanguages?.Split(',').FirstOrDefault()?.Trim() ?? "en";
        var url = $"{BaseUrl}/movie/{tmdbId}/videos?api_key={config.TmdbApiKey}&language={lang}";

        try
        {
            var response = await _httpClient.GetFromJsonAsync<TmdbVideoResponse>(url, cancellationToken);
            var videos = response?.Results?.ToList() ?? new List<TmdbVideo>();

            // If no videos in preferred language, try English fallback
            if (videos.Count == 0 && lang != "en")
            {
                url = $"{BaseUrl}/movie/{tmdbId}/videos?api_key={config.TmdbApiKey}&language=en";
                response = await _httpClient.GetFromJsonAsync<TmdbVideoResponse>(url, cancellationToken);
                videos = response?.Results?.ToList() ?? new List<TmdbVideo>();
            }

            // Also fetch videos without language filter to get all available
            url = $"{BaseUrl}/movie/{tmdbId}/videos?api_key={config.TmdbApiKey}";
            var allResponse = await _httpClient.GetFromJsonAsync<TmdbVideoResponse>(url, cancellationToken);
            var allVideos = allResponse?.Results?.ToList() ?? new List<TmdbVideo>();

            // Merge and deduplicate
            var merged = videos
                .Concat(allVideos)
                .DistinctBy(v => v.Key)
                .ToList();

            _logger.LogDebug("Found {Count} videos for movie {TmdbId}", merged.Count, tmdbId);
            return merged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch videos for movie {TmdbId}", tmdbId);
            return Array.Empty<TmdbVideo>();
        }
    }

    /// <summary>
    /// Gets videos for a TV show from TMDB.
    /// </summary>
    public async Task<IReadOnlyList<TmdbVideo>> GetTvShowVideosAsync(int tmdbId, string? language = null, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (string.IsNullOrEmpty(config?.TmdbApiKey))
        {
            _logger.LogWarning("TMDB API key not configured");
            return Array.Empty<TmdbVideo>();
        }

        // Extract first language from comma-separated list (e.g., "en-US,de-DE" -> "en-US")
        var lang = language ?? config.PreferredLanguages?.Split(',').FirstOrDefault()?.Trim() ?? "en";
        var url = $"{BaseUrl}/tv/{tmdbId}/videos?api_key={config.TmdbApiKey}&language={lang}";

        try
        {
            var response = await _httpClient.GetFromJsonAsync<TmdbVideoResponse>(url, cancellationToken);
            var videos = response?.Results?.ToList() ?? new List<TmdbVideo>();

            if (videos.Count == 0 && lang != "en")
            {
                url = $"{BaseUrl}/tv/{tmdbId}/videos?api_key={config.TmdbApiKey}&language=en";
                response = await _httpClient.GetFromJsonAsync<TmdbVideoResponse>(url, cancellationToken);
                videos = response?.Results?.ToList() ?? new List<TmdbVideo>();
            }

            _logger.LogDebug("Found {Count} videos for TV show {TmdbId}", videos.Count, tmdbId);
            return videos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch videos for TV show {TmdbId}", tmdbId);
            return Array.Empty<TmdbVideo>();
        }
    }

    /// <summary>
    /// Filters videos based on plugin configuration.
    /// </summary>
    public IReadOnlyList<TmdbVideo> FilterVideos(IEnumerable<TmdbVideo> videos)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return videos.ToList();

        var filtered = videos.Where(v =>
        {
            // Only YouTube and Vimeo are supported by yt-dlp
            if (v.Site != "YouTube" && v.Site != "Vimeo")
                return false;

            // Filter by official status if required
            if (config.OfficialVideosOnly && !v.Official)
                return false;

            return v.Type switch
            {
                "Trailer" => config.DownloadTrailers,
                "Teaser" => config.DownloadTeasers,
                "Featurette" => config.DownloadFeaturettes,
                "Behind the Scenes" => config.DownloadBehindTheScenes,
                "Clip" => config.DownloadClips,
                "Bloopers" => config.DownloadBloopers,
                "Interview" => config.DownloadInterviews,
                "Short" => config.DownloadShorts,
                "Opening Credits" => config.DownloadFeaturettes, // Group with featurettes
                _ => false
            };
        });

        // Prefer official videos if configured
        if (config.PreferOfficialVideos)
        {
            filtered = filtered.OrderByDescending(v => v.Official);
        }

        // Group by type and take max per type
        var grouped = filtered
            .GroupBy(v => v.Type)
            .SelectMany(g => g.Take(config.MaxVideosPerType));

        return grouped.ToList();
    }
}

#region TMDB Models

public class TmdbVideoResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("results")]
    public TmdbVideo[] Results { get; set; } = Array.Empty<TmdbVideo>();
}

public class TmdbVideo
{
    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    [JsonPropertyName("iso_3166_1")]
    public string? Country { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string Site { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("official")]
    public bool Official { get; set; }

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets the full URL for the video.
    /// </summary>
    public string Url => Site switch
    {
        "YouTube" => $"https://www.youtube.com/watch?v={Key}",
        "Vimeo" => $"https://vimeo.com/{Key}",
        _ => string.Empty
    };

    /// <summary>
    /// Maps TMDB video type to Jellyfin ExtraType suffix.
    /// </summary>
    public string JellyfinSuffix => Type switch
    {
        "Trailer" => "-trailer",
        "Teaser" => "-trailer",
        "Featurette" => "-featurette",
        "Behind the Scenes" => "-behindthescenes",
        "Clip" => "-scene",
        "Bloopers" => "-deletedscene",
        "Short" => "-short",
        "Interview" => "-interview",
        _ => "-other"
    };

    /// <summary>
    /// Gets the subfolder name for organizing extras (Plex-compatible naming).
    /// </summary>
    public string SubfolderName => Type switch
    {
        "Trailer" or "Teaser" => "Trailers",
        "Featurette" => "Featurettes",
        "Behind the Scenes" => "Behind The Scenes",
        "Clip" => "Scenes",
        "Bloopers" => "Deleted Scenes",
        "Short" => "Shorts",
        "Interview" => "Interviews",
        "Opening Credits" => "Featurettes",
        _ => "Other"
    };
}

#endregion
