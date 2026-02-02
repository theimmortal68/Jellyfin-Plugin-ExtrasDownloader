using Jellyfin.Plugin.ExtrasDownloader.Downloaders;
using Jellyfin.Plugin.ExtrasDownloader.Providers;
using Jellyfin.Plugin.ExtrasDownloader.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ExtrasDownloader;

/// <summary>
/// Registers plugin services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        // Register HTTP client factory
        services.AddHttpClient();

        // Core providers
        services.AddSingleton<TmdbVideoProvider>();
        services.AddSingleton<YtDlpDownloader>();

        // POT server management (for YouTube bot detection bypass)
        services.AddSingleton<PotServerManager>();

        // Download queue (tracks pending and processed items)
        services.AddSingleton<ExtrasDownloadQueue>();

        // Background services
        // LibraryMonitor: Hooks into Jellyfin library events, queues new items
        services.AddHostedService<LibraryMonitor>();
        // ExtrasDownloadService: Processes the queue, downloads extras
        services.AddHostedService<ExtrasDownloadService>();

        // Scheduled task is registered automatically by Jellyfin via reflection
    }
}
