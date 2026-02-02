using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExtrasDownloader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ExtrasDownloader;

/// <summary>
/// Plugin for downloading movie/TV extras from TMDB (via YouTube/Vimeo).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Extras Downloader";

    /// <inheritdoc />
    public override string Description => "Downloads trailers, featurettes, and behind-the-scenes content from TMDB/YouTube.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d2a0abca-4598-4d40-97bc-f74966738645");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
