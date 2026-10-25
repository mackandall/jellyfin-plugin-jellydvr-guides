using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.XmltvCleaner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.XmltvCleaner;

/// <summary>
/// XMLTV guide-cleaner plugin entry point. The dashboard icon comes from meta.json's
/// <c>imagePath</c> (a plain icon.png next to the DLL), not IHasEmbeddedImage, which a
/// side-loaded plugin can't use.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // This GUID must match meta.json and pluginUniqueId in Configuration/configPage.html.
    private static readonly Guid PluginId = Guid.Parse("110ae7e3-0c55-4684-b0f8-3e86eb2b53af");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        MoveToSources(Configuration);
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "JellyDVR Guides";

    public override string Description =>
        "Guide sources for Jellyfin's Live TV: XMLTV guides (cleaned of the problems Jellyfin " +
        "misreads, if asked) and Schedules Direct lineups fetched once a day, each served as " +
        "one guide for one Jellyfin guide provider.";

    public override Guid Id => PluginId;

    /// <summary>
    /// Settings from before sources (1.0's one guide URL, then the 1.1 preview's "More guides"
    /// URLs, "sd:&lt;lineup&gt;" for Schedules Direct) become sources: the first still answers at
    /// guide.xml, the rest at guide2.xml… as before.
    /// </summary>
    private void MoveToSources(PluginConfiguration cfg)
    {
        if (cfg.Sources.Length > 0 || (string.IsNullOrWhiteSpace(cfg.UpstreamUrl) && cfg.ExtraUpstreamUrls.Length == 0))
        {
            return;
        }

        var urls = new[] { cfg.UpstreamUrl }.Concat(cfg.ExtraUpstreamUrls).ToList();
        var sources = new List<GuideSource>();
        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i].Trim();
            if (url.Length == 0)
            {
                continue;
            }

            var sd = url.StartsWith("sd:", StringComparison.OrdinalIgnoreCase);
            sources.Add(new GuideSource
            {
                Id = i == 0 ? "main" : "guide" + (i + 1),
                Name = i == 0 ? "Main guide" : "Guide " + (i + 1),
                Type = sd ? GuideSource.SchedulesDirect : GuideSource.Xmltv,
                Url = sd ? string.Empty : url,
                Lineup = sd ? url[3..].Trim() : string.Empty,
                Days = cfg.SdDays,
                Clean = !sd
            });
        }

        cfg.Sources = sources.ToArray();
        cfg.UpstreamUrl = string.Empty;
        cfg.ExtraUpstreamUrls = Array.Empty<string>();
        SaveConfiguration();
    }

    /// <summary>The settings page and its script (the page's data-controller names the script).</summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
        };
        yield return new PluginPageInfo
        {
            Name = "JellyDvrGuidesJs",
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.js", GetType().Namespace)
        };
    }
}
