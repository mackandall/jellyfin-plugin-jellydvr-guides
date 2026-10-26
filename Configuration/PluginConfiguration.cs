using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyDvrGuides.Configuration;

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

    /// <summary>
    /// XMLTV sources: a guide request re-serves the last copy instead of fetching the source
    /// again if that copy is younger than this. Jellyfin itself re-downloads a guide at most
    /// once an hour, so this mainly guards against the guide address being asked repeatedly.
    /// </summary>
    public int MinRefetchMinutes { get; set; } = 10;

    /// <summary>XMLTV sources: seconds to wait for the source before falling back to the last good copy.</summary>
    public int UpstreamTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// The guide and picture addresses need no sign-in (Jellyfin downloads them without
    /// credentials), so by default they only answer the server itself and the home network
    /// (Jellyfin's own LAN settings, Dashboard › Networking). Off: anyone who can reach the
    /// server can read them.
    /// </summary>
    public bool LocalNetworkOnly { get; set; } = true;

    /// <summary>
    /// Schedules Direct: days before a kept programme picture is asked about again (the next time
    /// it's shown; downloaded again only if Schedules Direct lists a different one). 0 = never.
    /// </summary>
    public int RecheckPictureDays { get; set; } = 14;

    /// <summary>
    /// Schedules Direct: the time of day (minutes after midnight) of the daily update's default
    /// trigger, chosen at random once per server (see SdUpdateTask.PickMinuteOfDay) so servers
    /// don't all ask at the same moment. -1 = not chosen yet. Dashboard › Scheduled Tasks can
    /// still change the trigger.
    /// </summary>
    public int SdUpdateMinuteOfDay { get; set; } = -1;

    /// <summary>The Schedules Direct account every Schedules Direct source uses.</summary>
    public string SdUsername { get; set; } = string.Empty;

    /// <summary>
    /// SHA-1 of the Schedules Direct password, lowercase hex: what their API takes. The
    /// password itself is never stored (the settings page sends it once to be hashed).
    /// </summary>
    public string SdPasswordHash { get; set; } = string.Empty;

    // Settings from before sources, read once at start-up by Plugin.MoveToSources and then
    // emptied. Trimmed on set: the old page tended to leave stray whitespace on pasted values.
    private string _upstreamUrl = string.Empty;
    private string[] _extraUpstreamUrls = System.Array.Empty<string>();

    /// <summary>Migration only (1.0): the one XMLTV guide URL, now the first source.</summary>
    public string UpstreamUrl
    {
        get => _upstreamUrl;
        set => _upstreamUrl = value?.Trim() ?? string.Empty;
    }

    /// <summary>Migration only (1.1 preview): "More guides" URLs (guide2.xml…), now sources 2, 3…</summary>
    public string[] ExtraUpstreamUrls
    {
        get => _extraUpstreamUrls;
        set => _extraUpstreamUrls = (value ?? System.Array.Empty<string>())
            .Select(u => u?.Trim() ?? string.Empty)
            .Where(u => u.Length > 0)
            .ToArray();
    }

    /// <summary>Migration only (1.1 preview): days of Schedules Direct guide, now per source.</summary>
    public int SdDays { get; set; } = 14;
}
