using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyDvrGuides.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyDvrGuides;

/// <summary>
/// XMLTV guide-cleaner plugin entry point. The dashboard icon comes from meta.json's
/// <c>imagePath</c> (a plain icon.png next to the DLL), not IHasEmbeddedImage, which a
/// side-loaded plugin can't use.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's folder under the server's data folder (guide copies, Schedules Direct copy, pictures).</summary>
    public const string DataFolderName = "jellydvr-guides";

    // This GUID must match meta.json and pluginUniqueId in Configuration/configPage.js.
    private static readonly Guid PluginId = Guid.Parse("110ae7e3-0c55-4684-b0f8-3e86eb2b53af");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        CarryOverFromXmltvCleaner(applicationPaths);
        var changed = MoveToSources(Configuration);
        if (Configuration.SdUpdateMinuteOfDay < 0)
        {
            Configuration.SdUpdateMinuteOfDay = SchedulesDirect.SdUpdateTask.PickMinuteOfDay(Random.Shared);
            changed = true;
        }

        if (changed)
        {
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Before the rename the DLL was Jellyfin.Plugin.XmltvCleaner, so its settings were in
    /// Jellyfin.Plugin.XmltvCleaner.xml and its files in &lt;data&gt;/xmltv-cleaner. Copied (the
    /// old settings file stays as a backup) and moved once, before the settings are first read.
    /// </summary>
    private void CarryOverFromXmltvCleaner(IApplicationPaths paths)
    {
        var oldSettings = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.XmltvCleaner.xml");
        if (!File.Exists(ConfigurationFilePath) && File.Exists(oldSettings))
        {
            File.Copy(oldSettings, ConfigurationFilePath);
        }

        var oldData = Path.Combine(paths.DataPath, "xmltv-cleaner");
        var data = Path.Combine(paths.DataPath, DataFolderName);
        if (!Directory.Exists(data) && Directory.Exists(oldData))
        {
            Directory.Move(oldData, data);
        }
    }

    public override string Name => "JellyDVR Guides";

    public override string Description =>
        "Guide sources for Jellyfin's Live TV: XMLTV guides (cleaned of the problems Jellyfin " +
        "misreads, if asked) and Schedules Direct lineups fetched once a day, each served as " +
        "one guide for one Jellyfin guide provider.";

    public override Guid Id => PluginId;

    /// <summary>
    /// Settings from before sources (1.0's one guide URL, then the 1.1 preview's "More guides"
    /// URLs, "sd:&lt;lineup&gt;" for Schedules Direct) become sources: the first still answers at
    /// guide.xml, the rest at guide2.xml… as before. Returns whether anything moved.
    /// </summary>
    internal static bool MoveToSources(PluginConfiguration cfg)
    {
        if (cfg.Sources.Length > 0 || (string.IsNullOrWhiteSpace(cfg.UpstreamUrl) && cfg.ExtraUpstreamUrls.Length == 0))
        {
            return false;
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
        return true;
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
