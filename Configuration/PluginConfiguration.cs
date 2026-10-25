using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.XmltvCleaner.Configuration;

/// <summary>
/// One guide source: an XMLTV address or a Schedules Direct lineup, served as one guide at
/// guide/&lt;Id&gt;.xml for one Jellyfin guide provider.
/// </summary>
public class GuideSource
{
    public const string Xmltv = "xmltv";
    public const string SchedulesDirect = "sd";

    /// <summary>The address name: guide/&lt;Id&gt;.xml. Lower-case letters, digits and dashes.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="Xmltv"/> or <see cref="SchedulesDirect"/>.</summary>
    public string Type { get; set; } = Xmltv;

    /// <summary>XMLTV: the guide's address. Deliberately a setting, never a request parameter,
    /// so the anonymous guide address can't be used to fetch arbitrary URLs.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Schedules Direct: the lineup id, e.g. USA-OTA-73071.</summary>
    public string Lineup { get; set; } = string.Empty;

    /// <summary>Schedules Direct: days of guide from today.</summary>
    public int Days { get; set; } = 14;

    /// <summary>XMLTV: clean it at all (both fixes below are only applied when on).</summary>
    public bool Clean { get; set; } = true;

    /// <summary>Remove a year already in a movie title when it matches the programme's date.</summary>
    public bool CleanYears { get; set; } = true;

    /// <summary>Copy Gracenote (tms) episode ids to dd_progid, which Jellyfin reads.</summary>
    public bool CleanIds { get; set; } = true;

    /// <summary>
    /// Schedules Direct: write only the channel numbers <see cref="Tuners"/> have (a lineup has
    /// hundreds more, and Jellyfin re-reads the whole guide for every channel it refreshes).
    /// </summary>
    public bool OnlyTunerChannels { get; set; } = true;

    /// <summary>Keep a Jellyfin guide provider pointing here, limited to <see cref="Tuners"/>.</summary>
    public bool ManageProvider { get; set; }

    /// <summary>Jellyfin tuner ids the guide provider is for.</summary>
    public string[] Tuners { get; set; } = System.Array.Empty<string>();

    /// <summary>The Jellyfin guide provider this source keeps up (set by the plugin).</summary>
    public string ProviderId { get; set; } = string.Empty;
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The guide sources, in the order the settings page lists them.</summary>
    public GuideSource[] Sources { get; set; } = System.Array.Empty<GuideSource>();

    /// <summary>Jellyfin guide providers this plugin added or took over (removed with their source).</summary>
    public string[] ManagedProviderIds { get; set; } = System.Array.Empty<string>();

    // Trimmed on set: the config page tends to leave stray whitespace on pasted values.
    private string _upstreamUrl = string.Empty;

    /// <summary>
    /// Before sources (1.0): the one upstream, moved into <see cref="Sources"/> at start-up.
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
    /// Before sources (1.1 preview), moved into <see cref="Sources"/> at start-up.
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

    /// <summary>Before sources: days of Schedules Direct guide (now per source).</summary>
    public int SdDays { get; set; } = 14;
}
