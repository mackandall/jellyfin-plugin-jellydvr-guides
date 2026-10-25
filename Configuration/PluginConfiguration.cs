using System.Linq;
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

    private string[] _extraUpstreamUrls = System.Array.Empty<string>();

    /// <summary>
    /// More guides to clean, each served at its own address: the first at guide2.xml, the next
    /// at guide3.xml, and so on (e.g. a tuner's own guide, kept apart from the main one so each
    /// Jellyfin guide provider can be limited to its own tuner).
    /// </summary>
    public string[] ExtraUpstreamUrls
    {
        get => _extraUpstreamUrls;
        set => _extraUpstreamUrls = (value ?? System.Array.Empty<string>())
            .Select(u => u?.Trim() ?? string.Empty)
            .Where(u => u.Length > 0)
            .ToArray();
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

    /// <summary>
    /// The guide endpoint needs no sign-in (Jellyfin downloads it without credentials), so by
    /// default it only answers the server itself and the home network (Jellyfin's own LAN
    /// settings, Dashboard › Networking). Off: anyone who can reach the server can read it.
    /// </summary>
    public bool LocalNetworkOnly { get; set; } = true;

    /// <summary>Schedules Direct account name (a guide whose URL is "sd:&lt;lineup&gt;" comes from it).</summary>
    public string SdUsername { get; set; } = string.Empty;

    /// <summary>
    /// SHA-1 of the Schedules Direct password, lowercase hex: what their API takes. The
    /// password itself is never stored (the settings page sends it once to be hashed).
    /// </summary>
    public string SdPasswordHash { get; set; } = string.Empty;

    /// <summary>Days of guide to keep from Schedules Direct, from today.</summary>
    public int SdDays { get; set; } = 14;
}
