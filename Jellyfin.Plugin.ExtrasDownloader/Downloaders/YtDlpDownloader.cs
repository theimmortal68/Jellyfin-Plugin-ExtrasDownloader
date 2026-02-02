using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ExtrasDownloader.Configuration;
using Jellyfin.Plugin.ExtrasDownloader.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Downloaders;

/// <summary>
/// Downloads videos using yt-dlp.
/// Battle-tested options adapted from RandomNinjaAtk/arr-scripts.
/// </summary>
public class YtDlpDownloader
{
    private readonly ILogger<YtDlpDownloader> _logger;
    private static readonly Regex InvalidFileChars = new(@"[<>:""/\\|?*]", RegexOptions.Compiled);

    public YtDlpDownloader(ILogger<YtDlpDownloader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Downloads a video to the specified directory.
    /// </summary>
    /// <param name="video">The TMDB video metadata.</param>
    /// <param name="outputDirectory">The directory to save the video.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path to the downloaded file, or null if failed.</returns>
    public async Task<string?> DownloadAsync(
        TmdbVideo video,
        string outputDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogError("Plugin configuration not available");
            return null;
        }

        // Build safe filename - use video key as temp name to avoid special character issues
        var safeName = SanitizeFileName(video.Name);
        var suffix = video.JellyfinSuffix;

        // Download using video ID as filename (avoids special character issues)
        var tempOutputTemplate = Path.Combine(outputDirectory, $"{video.Key}.%(ext)s");
        var finalFileName = $"{safeName}{suffix}";

        // Build yt-dlp arguments
        var args = BuildArguments(video.Url, tempOutputTemplate, config);

        _logger.LogInformation("Downloading {VideoName} from {Url}", video.Name, video.Url);
        _logger.LogDebug("yt-dlp args: {Args}", args);

        try
        {
            var result = await RunYtDlpAsync(config.YtDlpPath, args, progress, cancellationToken);

            if (result.ExitCode != 0)
            {
                _logger.LogError("yt-dlp failed with exit code {ExitCode}: {Error}", result.ExitCode, result.StdErr);
                return null;
            }

            // Find the downloaded file (by video key)
            var downloadedFile = FindDownloadedFile(outputDirectory, video.Key, string.Empty);
            if (downloadedFile != null)
            {
                // Rename to final name with suffix
                var extension = Path.GetExtension(downloadedFile);
                var finalPath = Path.Combine(outputDirectory, $"{finalFileName}{extension}");

                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
                File.Move(downloadedFile, finalPath);

                _logger.LogInformation("Successfully downloaded: {FilePath}", finalPath);
                return finalPath;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {VideoName}", video.Name);
            return null;
        }
    }

    /// <summary>
    /// Checks if yt-dlp is available.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        var config = Plugin.Instance?.Configuration;
        var ytDlpPath = config?.YtDlpPath ?? "yt-dlp";

        try
        {
            var result = await RunYtDlpAsync(ytDlpPath, "--version", null, CancellationToken.None);
            if (result.ExitCode == 0)
            {
                _logger.LogInformation("yt-dlp version: {Version}", result.StdOut.Trim());
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp not available at: {Path}", ytDlpPath);
        }

        return false;
    }

    private static string BuildArguments(string url, string outputTemplate, PluginConfiguration config)
    {
        var sb = new StringBuilder();

        // Output template
        sb.Append($"-o \"{outputTemplate}\" ");

        // Format selection: bestvideo*+bestaudio/best (same as reference scripts)
        sb.Append($"-f \"{config.VideoFormat}\" ");

        // No video multistreams (from reference scripts)
        sb.Append("--no-video-multistreams ");

        // Merge output format (mkv recommended for compatibility)
        sb.Append($"--merge-output-format {config.OutputFormat} ");

        // Subtitles (from reference scripts)
        if (config.EmbedSubtitles)
        {
            // Extract language codes from PreferredLanguages (e.g., "en-US,de-DE" -> "en,de")
            var languages = config.PreferredLanguages
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Split('-')[0])
                .Distinct();
            var langList = string.Join(",", languages);

            sb.Append($"--write-sub --sub-lang {langList} --embed-subs ");
        }

        // Embed metadata
        sb.Append("--embed-metadata ");

        // Embed thumbnail as cover art
        sb.Append("--embed-thumbnail ");

        // Don't download if file exists
        sb.Append("--no-overwrites ");

        // Don't preserve mtime (from reference scripts)
        sb.Append("--no-mtime ");

        // Geo bypass (from reference scripts)
        sb.Append("--geo-bypass ");

        // Cookies file support (from reference scripts)
        if (!string.IsNullOrEmpty(config.CookiesFilePath) && File.Exists(config.CookiesFilePath))
        {
            sb.Append($"--cookies \"{config.CookiesFilePath}\" ");
        }

        // POT (Proof of Origin Token) provider support (from reference scripts)
        // This helps bypass YouTube's bot detection
        if (!string.IsNullOrEmpty(config.PotProviderUrl))
        {
            sb.Append($"--extractor-args \"youtubepot-bgutilhttp:base_url={config.PotProviderUrl}\" ");
        }

        // Sleep intervals for rate limiting (from reference scripts)
        sb.Append($"--sleep-interval {config.YtDlpSleepInterval} ");
        sb.Append($"--max-sleep-interval {config.YtDlpMaxSleepInterval} ");

        // Progress output for parsing
        sb.Append("--newline --progress ");

        // The URL
        sb.Append($"\"{url}\"");

        return sb.ToString();
    }

    private async Task<ProcessResult> RunYtDlpAsync(
        string ytDlpPath,
        string arguments,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stdOut.AppendLine(e.Data);

            // Parse progress from yt-dlp output
            if (progress != null && e.Data.Contains('%'))
            {
                var match = Regex.Match(e.Data, @"(\d+\.?\d*)%");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
                {
                    progress.Report(pct / 100.0);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdErr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = InvalidFileChars.Replace(name, "_");
        // Also replace multiple spaces/underscores with single underscore
        sanitized = Regex.Replace(sanitized, @"[\s_]+", "_");
        // Trim underscores from ends
        sanitized = sanitized.Trim('_');
        // Limit length
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }
        return sanitized;
    }

    private static string? FindDownloadedFile(string directory, string baseName, string suffix)
    {
        if (!Directory.Exists(directory)) return null;

        var pattern = $"{baseName}{suffix}.*";
        var files = Directory.GetFiles(directory, pattern);

        // Prefer mp4, then mkv, then webm
        var preferredExtensions = new[] { ".mp4", ".mkv", ".webm" };
        foreach (var ext in preferredExtensions)
        {
            var match = files.FirstOrDefault(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return files.FirstOrDefault();
    }

    private record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
