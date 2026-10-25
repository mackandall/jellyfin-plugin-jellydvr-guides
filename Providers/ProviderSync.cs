using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.XmltvCleaner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XmltvCleaner.Providers;

/// <summary>
/// Keeps one Jellyfin XMLTV guide provider per source that asks for it, pointing at the
/// source's guide address and limited to the source's tuners (a guide left on "all tuners"
/// gives every tuner's channels the wrong guide). A provider already pointing at the address,
/// or at the old 1.0 address for the first source, is taken over rather than duplicated.
/// Providers the plugin made are removed with their source.
/// </summary>
public sealed class ProviderSync
{
    private readonly IConfigurationManager _config;
    private readonly IListingsManager _listings;
    private readonly ITaskManager _tasks;
    private readonly ILogger<ProviderSync> _logger;

    public ProviderSync(IConfigurationManager config, IListingsManager listings, ITaskManager tasks, ILogger<ProviderSync> logger)
    {
        _config = config;
        _listings = listings;
        _tasks = tasks;
        _logger = logger;
    }

    public sealed record Tuner(string Id, string Name, string Type);

    public sealed record ProviderState(string SourceId, bool InJellyfin, string[] Tuners, bool AllTuners, string? ProviderId, IReadOnlyList<OtherProvider> Others);

    /// <summary>Another guide provider covering some of the same tuners (Jellyfin uses only the first one it finds).</summary>
    public sealed record OtherProvider(string Id, string Type, string Where, string[] Tuners, bool AllTuners, bool ComesFirst);

    public IReadOnlyList<Tuner> Tuners() =>
        LiveTv().TunerHosts
            .Select(t => new Tuner(t.Id, string.IsNullOrWhiteSpace(t.FriendlyName) ? TypeName(t.Type) + " at " + t.Url : t.FriendlyName, t.Type))
            .ToList();

    private static string TypeName(string type) => type switch
    {
        "hdhomerun" => "HDHomeRun",
        "m3u" => "M3U tuner",
        _ => type
    };

    /// <summary>
    /// The server address guide providers should use: the one an existing provider already
    /// reaches this plugin at (it's known to work for Jellyfin and for clients loading the
    /// programme pictures), else the address the settings page was opened at.
    /// </summary>
    public string ServerBase(string requestBase)
    {
        foreach (var p in LiveTv().ListingProviders.Where(p => p.Type == "xmltv" && p.Path is not null))
        {
            foreach (var marker in new[] { "/JellyDvrGuides/", "/XmltvCleaner/" })
            {
                var at = p.Path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at > 0 && p.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return p.Path[..(at + 1)];
                }
            }
        }

        return requestBase;
    }

    /// <summary>The source's guide address as Jellyfin should fetch it.</summary>
    public static string AddressOf(string serverBase, GuideSource source) => serverBase + "JellyDvrGuides/guide/" + source.Id + ".xml";

    /// <summary>Which Jellyfin guide provider (if any) reads each source, and for which tuners.</summary>
    public IReadOnlyList<ProviderState> States(PluginConfiguration cfg)
    {
        var providers = LiveTv().ListingProviders;
        return cfg.Sources.Select((s, i) =>
        {
            var p = Match(providers, s, i);
            return new ProviderState(s.Id, p is not null, p?.EnabledTuners ?? Array.Empty<string>(), p?.EnableAllTuners ?? false, p?.Id, Others(providers, p, s));
        }).ToList();
    }

    // The other providers that also give a guide to this source's tuners.
    internal static List<OtherProvider> Others(ListingsProviderInfo[] providers, ListingsProviderInfo? mine, GuideSource source)
    {
        var tuners = mine is not null ? mine.EnabledTuners : source.Tuners;
        var all = mine?.EnableAllTuners ?? false;
        var myIndex = mine is null ? int.MaxValue : Array.IndexOf(providers, mine);
        return providers
            .Select((o, i) => (o, i))
            .Where(x => x.o != mine && (all ? true : x.o.EnableAllTuners ? tuners.Length > 0 : x.o.EnabledTuners.Intersect(tuners).Any()))
            .Select(x => new OtherProvider(
                x.o.Id,
                x.o.Type,
                x.o.Type == "xmltv" ? x.o.Path ?? string.Empty : "Schedules Direct " + x.o.ListingsId,
                x.o.EnableAllTuners ? Array.Empty<string>() : x.o.EnabledTuners.Intersect(all ? x.o.EnabledTuners : tuners).ToArray(),
                x.o.EnableAllTuners,
                x.i < myIndex))
            .ToList();
    }

    /// <summary>
    /// Removes another guide provider that covers a source's tuners (the source card's Remove
    /// button). Only one listed as covering a source's tuners can be removed this way.
    /// </summary>
    public bool RemoveOther(PluginConfiguration cfg, string providerId)
    {
        if (!States(cfg).Any(s => s.Others.Any(o => o.Id == providerId)))
        {
            return false;
        }

        _listings.DeleteListingsProvider(providerId);
        _logger.LogInformation("Removed guide provider {Id} that covered a guide source's tuners", providerId);
        QueueGuideRefresh();
        return true;
    }

    /// <summary>
    /// Makes Jellyfin's guide providers match the sources; returns what changed. Queues
    /// Jellyfin's Refresh Guide when anything did.
    /// </summary>
    public async Task<List<string>> ApplyAsync(Plugin plugin, string serverBase)
    {
        var cfg = plugin.Configuration;
        var changes = new List<string>();
        var providers = LiveTv().ListingProviders.ToList();
        var template = providers.FirstOrDefault(p => p.Type == "xmltv");

        for (var i = 0; i < cfg.Sources.Length; i++)
        {
            var source = cfg.Sources[i];
            if (!source.ManageProvider)
            {
                continue;
            }

            var address = AddressOf(serverBase, source);
            var existing = Match(providers, source, i);
            var info = existing is null ? NewProvider(template) : Copy(existing);
            var tuners = source.Tuners.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray();
            if (existing is not null && existing.Path == address && !existing.EnableAllTuners
                && existing.EnabledTuners.OrderBy(t => t).SequenceEqual(tuners.OrderBy(t => t)))
            {
                source.ProviderId = existing.Id;
                continue;
            }

            info.Path = address;
            info.EnabledTuners = tuners;
            info.EnableAllTuners = false;
            var saved = await _listings.SaveListingProvider(info, false, false).ConfigureAwait(false);
            source.ProviderId = saved.Id;
            if (!cfg.ManagedProviderIds.Contains(saved.Id))
            {
                cfg.ManagedProviderIds = cfg.ManagedProviderIds.Append(saved.Id).ToArray();
            }

            changes.Add((existing is null ? "Added" : "Updated") + $" the guide provider for \"{source.Name}\"");
            _logger.LogInformation("Guide provider for source {Source}: {Path}, tuners {Tuners}", source.Id, address, string.Join(", ", tuners));
        }

        // Providers this plugin made whose source is gone.
        foreach (var id in cfg.ManagedProviderIds.ToList())
        {
            if (cfg.Sources.Any(s => s.ProviderId == id))
            {
                continue;
            }

            if (providers.Any(p => p.Id == id))
            {
                _listings.DeleteListingsProvider(id);
                changes.Add("Removed a guide provider whose source was deleted");
            }

            cfg.ManagedProviderIds = cfg.ManagedProviderIds.Where(x => x != id).ToArray();
        }

        plugin.SaveConfiguration();
        if (changes.Count > 0)
        {
            QueueGuideRefresh();
        }

        return changes;
    }

    private void QueueGuideRefresh()
    {
        var refresh = _tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
        if (refresh is not null)
        {
            // Queued, not Execute: Execute does nothing while a refresh is already running.
            _tasks.QueueScheduledTask(refresh.ScheduledTask, new TaskOptions());
        }
    }

    // The provider reading this source: the one it made, else one at its address, else one at
    // its address from before sources.
    internal static ListingsProviderInfo? Match(IEnumerable<ListingsProviderInfo> providers, GuideSource source, int index)
    {
        var list = providers.Where(p => p.Type == "xmltv").ToList();
        var legacy = LegacyFeed(source, index);
        return list.FirstOrDefault(p => !string.IsNullOrEmpty(source.ProviderId) && p.Id == source.ProviderId)
            ?? list.FirstOrDefault(p => SamePath(p.Path, "JellyDvrGuides/guide/" + source.Id + ".xml") || SamePath(p.Path, "XmltvCleaner/guide/" + source.Id + ".xml"))
            ?? list.FirstOrDefault(p => SamePath(p.Path, legacy == 1 ? "XmltvCleaner/guide.xml" : $"XmltvCleaner/guide{legacy}.xml"));

        static bool SamePath(string? path, string tail) =>
            path is not null && path.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase);
    }

    // Which pre-sources address (1 = guide.xml, 2 = guide2.xml…) means this source: the one
    // migration named it after, else its place in the list (as GuideController.LegacySource).
    private static int LegacyFeed(GuideSource source, int index) =>
        source.Id == "main" ? 1
        : source.Id.StartsWith("guide", StringComparison.Ordinal) && int.TryParse(source.Id[5..], out var n) && n >= 2 ? n
        : index + 1;

    // A new XMLTV provider with the category lists of the one already there (so Jellyfin sorts
    // movies, sports and series the same way), else the dashboard's defaults.
    private static ListingsProviderInfo NewProvider(ListingsProviderInfo? template) => new()
    {
        Type = "xmltv",
        MovieCategories = template?.MovieCategories ?? new[] { "movie" },
        SportsCategories = template?.SportsCategories ?? new[] { "sports", "basketball", "baseball", "football" },
        KidsCategories = template?.KidsCategories ?? new[] { "kids", "family", "children" },
        NewsCategories = template?.NewsCategories ?? new[] { "news" },
        MoviePrefix = template?.MoviePrefix,
        PreferredLanguage = template?.PreferredLanguage,
        ChannelMappings = Array.Empty<MediaBrowser.Model.Dto.NameValuePair>()
    };

    private static ListingsProviderInfo Copy(ListingsProviderInfo p) => new()
    {
        Id = p.Id,
        Type = p.Type,
        Username = p.Username,
        Password = p.Password,
        ListingsId = p.ListingsId,
        ZipCode = p.ZipCode,
        Country = p.Country,
        Path = p.Path,
        EnabledTuners = p.EnabledTuners,
        EnableAllTuners = p.EnableAllTuners,
        NewsCategories = p.NewsCategories,
        SportsCategories = p.SportsCategories,
        KidsCategories = p.KidsCategories,
        MovieCategories = p.MovieCategories,
        ChannelMappings = p.ChannelMappings,
        MoviePrefix = p.MoviePrefix,
        PreferredLanguage = p.PreferredLanguage,
        UserAgent = p.UserAgent
    };

    private LiveTvOptions LiveTv() => _config.GetConfiguration<LiveTvOptions>("livetv");
}
