using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.XmltvCleaner.Configuration;
using Jellyfin.Plugin.XmltvCleaner.SchedulesDirect;
using Jellyfin.Plugin.XmltvCleaner.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XmltvCleaner.Guides;

/// <summary>
/// Serves each guide source as one guide file, keeping the last good copy on disk
/// (guide-&lt;id&gt;.xml under the server's data folder). An XMLTV source is fetched (at most
/// every MinRefetchMinutes) and, if asked, run through <see cref="GuideCleaner"/>; a Schedules
/// Direct source is written from the local copy <see cref="SdUpdater"/> keeps, never fetched
/// from Schedules Direct on a guide request.
///
/// Serving on demand (rather than writing files on a separate schedule) means Jellyfin's own
/// "Refresh Guide" task always gets data as fresh as the source, with no second schedule to
/// keep in step with it.
///
/// Why the last-good fallback matters: Jellyfin's XmlTvListingsProvider never checks the
/// HTTP status of an XMLTV URL (confirmed by decompiling Jellyfin.LiveTv 12.0.0) — it
/// caches whatever body comes back as the guide file. So a transient failure is answered
/// with the previous copy, never an error page, whenever one exists.
/// </summary>
public sealed class GuideService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SdUpdater _sd;
    private readonly TunerChannels _tunerChannels;
    private readonly GuideCopies _copies;
    private readonly ILogger<GuideService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, RefreshStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    public GuideService(IHttpClientFactory httpClientFactory, GuideCopies copies, SdUpdater sd, TunerChannels tunerChannels, ILogger<GuideService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _copies = copies;
        _sd = sd;
        _tunerChannels = tunerChannels;
        _logger = logger;
    }

    public sealed record RefreshStatus(
        DateTime? FetchedUtc,
        int Programmes,
        int TitlesCleaned,
        int ProgramIdsAdded,
        IReadOnlyList<string> Samples,
        long DurationMs,
        string? LastError,
        DateTime? LastErrorUtc,
        int Channels = 0,
        int ChannelsInLineup = 0);

    public static GuideSource? Find(PluginConfiguration cfg, string id) =>
        cfg.Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The source's last fetch or write. Before the first one since the server started, the
    /// saved copy's time stands in (Programmes 0: not counted), since a fresh copy is served
    /// without being fetched again.
    /// </summary>
    public RefreshStatus? StatusOf(string id)
    {
        lock (_statuses)
        {
            if (_statuses.TryGetValue(id, out var status))
            {
                return status;
            }
        }

        try
        {
            var path = _copies.PathOf(id);
            return File.Exists(path) ? new RefreshStatus(File.GetLastWriteTimeUtc(path), 0, 0, 0, Array.Empty<string>(), 0, null, null) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void SetStatus(string id, RefreshStatus status)
    {
        lock (_statuses)
        {
            _statuses[id] = status;
        }
    }

    /// <summary>
    /// Returns the path of a guide file to serve for a source, refreshing it first unless the
    /// current copy is fresh (or <paramref name="force"/> is set). Throws only when the source
    /// fails AND no previous copy exists.
    /// </summary>
    /// <param name="imageBase">The server's address as Jellyfin reached it ("http://host:8096/"), for Schedules Direct guides' picture links.</param>
    public async Task<string> GetGuidePathAsync(string id, bool force, CancellationToken cancellationToken, string? imageBase = null)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var source = Find(cfg, id) ?? throw new InvalidOperationException($"There is no guide source \"{id}\" (plugin settings).");
        var guidePath = _copies.PathOf(source.Id);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fromSd = source.Type == GuideSource.SchedulesDirect;
            // A whole lineup when the tuners list nothing (or none are ticked).
            var numbers = fromSd && source.OnlyTunerChannels
                ? await _tunerChannels.NumbersAsync(source.Tuners, "guide " + source.Id, cancellationToken).ConfigureAwait(false)
                : null;
            var stamp = Path.Combine(_sd.Root, "updated");
            // A copy made under other settings (type, address, lineup, cleaning) is never fresh.
            var sameSettings = GuideCopies.MadeFrom(guidePath, source, numbers);
            var fresh = sameSettings && (fromSd
                // Schedules Direct: rewritten only after the local copy was updated.
                ? File.Exists(guidePath) && File.Exists(stamp) && File.GetLastWriteTimeUtc(guidePath) >= File.GetLastWriteTimeUtc(stamp)
                : File.Exists(guidePath) && File.GetLastWriteTimeUtc(guidePath) >= DateTime.UtcNow.AddMinutes(-Math.Max(0, cfg.MinRefetchMinutes)));
            if (!force && fresh)
            {
                return guidePath;
            }

            try
            {
                if (fromSd)
                {
                    // A lineup just put in a source and not downloaded yet: download it now
                    // (Jellyfin would otherwise keep the error reply as its guide). The updater
                    // only goes to Schedules Direct when the lineups in use changed or a day passed.
                    var lineup = source.Lineup.Trim();
                    if (!File.Exists(_sd.LineupPath(lineup)) || !File.Exists(stamp) || !File.ReadAllText(stamp).Split(',').Contains(lineup, StringComparer.OrdinalIgnoreCase))
                    {
                        await _sd.UpdateAsync(false, null, cancellationToken).ConfigureAwait(false);
                    }

                    WriteFromSd(source, guidePath, imageBase ?? "http://localhost:8096/", numbers, cancellationToken);
                }
                else
                {
                    await RefreshAsync(source, guidePath, cfg, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                SetStatus(source.Id, (StatusOf(source.Id) ?? new RefreshStatus(null, 0, 0, 0, Array.Empty<string>(), 0, null, null))
                    with { LastError = ex.Message, LastErrorUtc = DateTime.UtcNow });

                if (File.Exists(guidePath))
                {
                    _logger.LogWarning(ex, "Refreshing guide {Source} failed; serving the last good copy from {Time:u}",
                        source.Id, File.GetLastWriteTimeUtc(guidePath));
                    return guidePath;
                }

                _logger.LogError(ex, "Refreshing guide {Source} failed and no previous copy exists", source.Id);
                throw;
            }

            return guidePath;
        }
        finally
        {
            _lock.Release();
        }
    }

    // A Schedules Direct lineup from the local copy. No cleaning: the writer gives Jellyfin
    // what it needs already.
    private void WriteFromSd(GuideSource source, string guidePath, string imageBase, ISet<string>? numbers, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_copies.Directory);
        var tempPath = guidePath + ".tmp";
        var sw = Stopwatch.StartNew();
        try
        {
            int programmes, channels, inLineup;
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                (programmes, channels, inLineup) = SdGuideWriter.Write(_sd, source.Lineup.Trim(), Math.Clamp(source.Days, 1, 21), imageBase, output, cancellationToken, numbers);
            }

            if (programmes == 0)
            {
                throw new InvalidDataException($"Schedules Direct lineup {source.Lineup} has no programmes in the local copy.");
            }

            File.Move(tempPath, guidePath, overwrite: true);
            GuideCopies.RecordMadeFrom(guidePath, source, numbers);
            sw.Stop();
            SetStatus(source.Id, new RefreshStatus(DateTime.UtcNow, programmes, 0, 0, Array.Empty<string>(), sw.ElapsedMilliseconds, null, null, channels, inLineup));
            _logger.LogInformation(
                "Wrote guide {Source} from Schedules Direct lineup {Lineup}: {Channels} of {InLineup} channels, {Programmes} programmes ({Ms} ms)",
                source.Id, source.Lineup, channels, inLineup, programmes, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task RefreshAsync(GuideSource source, string guidePath, PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            throw new InvalidOperationException($"Guide source \"{source.Name}\" has no XMLTV address (plugin settings).");
        }

        Directory.CreateDirectory(_copies.Directory);
        var tempPath = guidePath + ".tmp";
        var sw = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, cfg.UpstreamTimeoutSeconds)));

        GuideCleaner.CleanResult result;
        try
        {
            using var response = await _httpClientFactory.CreateClient()
                .GetAsync(source.Url.Trim(), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (output.ConfigureAwait(false))
                {
                    // XmlReader over a network stream is synchronous; run it off the
                    // request thread. Cleaning off: passed through, only checked and counted.
                    result = await Task.Run(
                            () => GuideCleaner.Clean(input, output, timeout.Token, source.Clean && source.CleanYears, source.Clean && source.CleanIds),
                            timeout.Token)
                        .ConfigureAwait(false);
                }
            }

            // A well-formed but empty guide almost certainly means a hiccup at the source;
            // don't let it replace a good copy.
            if (result.Programmes == 0)
            {
                throw new InvalidDataException("The guide contained no <programme> entries.");
            }

            File.Move(tempPath, guidePath, overwrite: true);
            GuideCopies.RecordMadeFrom(guidePath, source, null);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        sw.Stop();
        SetStatus(source.Id, new RefreshStatus(DateTime.UtcNow, result.Programmes, result.TitlesCleaned, result.ProgramIdsAdded, result.Samples, sw.ElapsedMilliseconds, null, null));
        _logger.LogInformation(
            "Fetched guide {Source}: {Programmes} programmes, {Cleaned} titles had a duplicate year stripped, {Ids} tms ids copied to dd_progid ({Ms} ms)",
            source.Id, result.Programmes, result.TitlesCleaned, result.ProgramIdsAdded, sw.ElapsedMilliseconds);
    }
}
