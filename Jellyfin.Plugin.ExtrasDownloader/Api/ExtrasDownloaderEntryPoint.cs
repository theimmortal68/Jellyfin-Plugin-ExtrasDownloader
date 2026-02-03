using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtrasDownloader.Api;

/// <summary>
/// Entry point that injects the "Download Extras" menu item into Jellyfin's web UI.
/// </summary>
public class ExtrasDownloaderEntryPoint : IServerEntryPoint
{
    private readonly ILogger<ExtrasDownloaderEntryPoint> _logger;

    public ExtrasDownloaderEntryPoint(ILogger<ExtrasDownloaderEntryPoint> logger)
    {
        _logger = logger;
    }

    public Task RunAsync()
    {
        _logger.LogInformation("Extras Downloader entry point initialized");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
