using System;
using System.Collections.Generic;
using System.Globalization;
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
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "XMLTV Guide Cleaner";

    public override string Description =>
        "Serves a cleaned copy of an upstream XMLTV guide, stripping a year already baked " +
        "into a movie's title so recordings aren't named \"Movie (2001) (2001)\".";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
