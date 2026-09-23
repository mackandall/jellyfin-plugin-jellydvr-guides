using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XmltvCleaner.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Trimmed on set: the config page tends to leave stray whitespace on pasted values.
    private string _upstreamUrl = string.Empty;

    /// <summary>
    /// The original XMLTV guide URL (what Jellyfin's XMLTV listings provider pointed at
    /// before switching it to this plugin's endpoint), e.g. a Channels DVR
    /// <c>/devices/&lt;id&gt;/guide/xmltv?duration=1209600</c> URL. Deliberately a config
    /// value rather than a query parameter on the anonymous guide endpoint, so that
    /// endpoint can't be used as an open proxy to arbitrary URLs.
    /// </summary>
    public string UpstreamUrl
    {
        get => _upstreamUrl;
        set => _upstreamUrl = value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// A request to the guide endpoint re-serves the last cleaned copy instead of
    /// re-fetching upstream if that copy is younger than this. Jellyfin itself only
    /// re-downloads the guide at most once an hour, so this mainly guards against the
    /// anonymous endpoint being hit repeatedly.
    /// </summary>
    public int MinRefetchMinutes { get; set; } = 10;

    /// <summary>Seconds to wait for the upstream guide before falling back to the last good copy.</summary>
    public int UpstreamTimeoutSeconds { get; set; } = 120;
}
