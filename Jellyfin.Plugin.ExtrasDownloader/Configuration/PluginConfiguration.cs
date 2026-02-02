using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExtrasDownloader.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ===================
    // API Configuration
    // ===================

    /// <summary>
    /// Gets or sets the TMDB API key.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    // ===================
    // yt-dlp Configuration
    // ===================

    /// <summary>
    /// Gets or sets the path to yt-dlp executable.
    /// </summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Gets or sets the path to cookies.txt file for YouTube authentication.
    /// </summary>
    public string CookiesFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the POT (Proof of Origin Token) provider URL for YouTube bot detection bypass.
    /// Example: http://localhost:4416
    /// </summary>
    public string PotProviderUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video format string for yt-dlp.
    /// Default matches script: bestvideo*+bestaudio/best
    /// </summary>
    public string VideoFormat { get; set; } = "bestvideo*+bestaudio/best";

    // ===================
    // Content Type Selection
    // ===================

    /// <summary>
    /// Gets or sets whether to download trailers.
    /// </summary>
    public bool DownloadTrailers { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to download teasers.
    /// </summary>
    public bool DownloadTeasers { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to download featurettes.
    /// </summary>
    public bool DownloadFeaturettes { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to download behind the scenes content.
    /// </summary>
    public bool DownloadBehindTheScenes { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to download clips.
    /// </summary>
    public bool DownloadClips { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to download bloopers.
    /// </summary>
    public bool DownloadBloopers { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to download interviews.
    /// </summary>
    public bool DownloadInterviews { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to download shorts.
    /// </summary>
    public bool DownloadShorts { get; set; } = false;

    // ===================
    // Filtering Options
    // ===================

    /// <summary>
    /// Gets or sets the maximum number of videos per type to download.
    /// </summary>
    public int MaxVideosPerType { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether to prefer official videos over fan uploads.
    /// </summary>
    public bool PreferOfficialVideos { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to only download official videos.
    /// </summary>
    public bool OfficialVideosOnly { get; set; } = false;

    /// <summary>
    /// Gets or sets the preferred language for videos (e.g., "en-US", "de-DE").
    /// Multiple languages can be comma-separated.
    /// </summary>
    public string PreferredLanguages { get; set; } = "en-US";

    /// <summary>
    /// Gets or sets whether to skip items that already have local extras.
    /// </summary>
    public bool SkipExistingExtras { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to only process items missing trailers.
    /// </summary>
    public bool OnlyMissingTrailers { get; set; } = false;

    // ===================
    // Output Configuration
    // ===================

    /// <summary>
    /// Gets or sets whether to organize extras into subfolders by type.
    /// Uses Plex-compatible naming: Trailers, Featurettes, Behind The Scenes, etc.
    /// </summary>
    public bool OrganizeIntoFolders { get; set; } = true;

    /// <summary>
    /// Gets or sets the output container format.
    /// </summary>
    public string OutputFormat { get; set; } = "mkv";

    /// <summary>
    /// Gets or sets whether to embed subtitles in the video file.
    /// </summary>
    public bool EmbedSubtitles { get; set; } = true;

    // ===================
    // Rate Limiting
    // ===================

    /// <summary>
    /// Gets or sets the sleep interval between video downloads (seconds).
    /// </summary>
    public int SleepBetweenVideos { get; set; } = 60;

    /// <summary>
    /// Gets or sets the sleep interval between processing items (seconds).
    /// </summary>
    public int SleepBetweenItems { get; set; } = 300;

    /// <summary>
    /// Gets or sets the yt-dlp minimum sleep interval for requests (seconds).
    /// </summary>
    public int YtDlpSleepInterval { get; set; } = 30;

    /// <summary>
    /// Gets or sets the yt-dlp maximum sleep interval for requests (seconds).
    /// </summary>
    public int YtDlpMaxSleepInterval { get; set; } = 120;

    // ===================
    // Tracking
    // ===================

    /// <summary>
    /// Gets or sets the number of days to remember processed items.
    /// Items will be re-checked after this period.
    /// </summary>
    public int ProcessedItemRetentionDays { get; set; } = 7;
}
