using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.XmltvCleaner.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XmltvCleaner.SchedulesDirect;

/// <summary>
/// Keeps a local copy of the account's Schedules Direct guide, brought up to date about once a
/// day (the scheduled task, or Update now). Each station-day carries a fingerprint, so after
/// the first download only the days that changed are fetched, and a programme's details only
/// when its own fingerprint changed. Guides are then written from this copy as often as
/// Jellyfin likes without asking Schedules Direct anything.
///
/// Laid out under the server's data folder, xmltv-cleaner/sd/: lineups/&lt;lineup&gt;.json,
/// schedules/&lt;station&gt;_&lt;date&gt;.json, programs/&lt;programID&gt;.json.
/// </summary>
public sealed class SdUpdater
{
    // Schedules Direct's batch limits: 5000 station ids or programme ids a request.
    private const int StationBatch = 1000;
    private const int ProgramBatch = 2000;

    private readonly SdClient _client;
    private readonly ILogger<SdUpdater> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SdUpdater(SdClient client, IApplicationPaths paths, ILogger<SdUpdater> logger)
    {
        _client = client;
        _logger = logger;
        Root = Path.Combine(paths.DataPath, "xmltv-cleaner", "sd");
    }

    public string Root { get; }

    public sealed record UpdateStatus(
        DateTime? FinishedUtc,
        int Lineups,
        int Stations,
        int DaysFetched,
        int ProgramsFetched,
        long DurationMs,
        string? Error,
        DateTime? ErrorUtc);

    public UpdateStatus? LastStatus { get; private set; }

    public string LineupPath(string lineup) => Path.Combine(Root, "lineups", Safe(lineup) + ".json");

    public string SchedulePath(string station, string date) => Path.Combine(Root, "schedules", Safe(station) + "_" + date + ".json");

    public string ProgramPath(string programId) => Path.Combine(Root, "programs", Safe(programId) + ".json");

    /// <summary>The account's lineups as last fetched (from the cached status).</summary>
    public JsonArray? CachedLineups()
    {
        var path = Path.Combine(Root, "lineups.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray : null;
    }

    /// <summary>
    /// Brings the local copy up to date. Unless forced, does nothing if the last good update
    /// was under 20 hours ago (a restart or a second trigger never means a second download).
    /// </summary>
    public async Task UpdateAsync(bool force, IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Only the lineups a source uses are downloaded; the rest are only listed.
            var sdSources = cfg.Sources.Where(x => x.Type == GuideSource.SchedulesDirect && x.Lineup.Trim().Length > 0).ToList();
            var used = sdSources.Select(x => x.Lineup.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var usedList = string.Join(",", used.OrderBy(u => u, StringComparer.OrdinalIgnoreCase));

            // The stamp holds the lineups it covered: a guide switched to another lineup updates at once.
            var stamp = Path.Combine(Root, "updated");
            if (!force && File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow.AddHours(-20)
                && File.ReadAllText(stamp) == usedList)
            {
                return;
            }

            Directory.CreateDirectory(Path.Combine(Root, "lineups"));
            Directory.CreateDirectory(Path.Combine(Root, "schedules"));
            Directory.CreateDirectory(Path.Combine(Root, "programs"));

            // Status: is the service up, and which lineups changed.
            var status = await _client.GetAsync(cfg, "status", ct).ConfigureAwait(false);
            var system = status["systemStatus"]?.AsArray().FirstOrDefault();
            if (system?["status"]?.ToString() is { } state && !state.Equals("Online", StringComparison.OrdinalIgnoreCase))
            {
                throw new SdException(3000, $"Schedules Direct is {state}: {system["message"]}");
            }

            var lineups = (status["lineups"]?.AsArray() ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(l => l["isDeleted"]?.GetValue<bool>() != true && l["lineup"] is not null)
                .ToList();
            var listed = await _client.GetAsync(cfg, "lineups", ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(Root, "lineups.json"), (listed["lineups"] ?? new JsonArray()).ToJsonString());
            progress?.Report(5);

            lineups = lineups.Where(l => used.Contains(l["lineup"]!.ToString())).ToList();

            var stations = new HashSet<string>();
            foreach (var l in lineups)
            {
                var id = l["lineup"]!.ToString();
                var path = LineupPath(id);
                var modified = l["modified"]?.ToString();
                JsonNode? map = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
                if (map is null || map["metadata"]?["modified"]?.ToString() != modified)
                {
                    map = await _client.GetAsync(cfg, "lineups/" + id, ct).ConfigureAwait(false);
                    File.WriteAllText(path, map.ToJsonString());
                    _logger.LogInformation("Schedules Direct: lineup {Lineup} fetched", id);
                }

                foreach (var e in map["map"]?.AsArray() ?? new JsonArray())
                {
                    if (e?["stationID"]?.ToString() is { } s)
                    {
                        stations.Add(s);
                    }
                }
            }

            progress?.Report(10);
            var days = sdSources.Count == 0 ? 1 : Math.Clamp(sdSources.Max(x => x.Days), 1, 21);
            var dates = Enumerable.Range(0, days).Select(d => DateTime.UtcNow.Date.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();

            // Fingerprints of every station-day; only the ones that differ are fetched.
            var wanted = new List<(string Station, string Date)>();
            foreach (var batch in stations.Chunk(StationBatch))
            {
                var body = new JsonArray(batch.Select(s => (JsonNode)new JsonObject { ["stationID"] = s, ["date"] = new JsonArray(dates.Select(d => (JsonNode)d).ToArray()) }).ToArray());
                var md5s = await _client.PostAsync(cfg, "schedules/md5", body, ct).ConfigureAwait(false);
                foreach (var s in batch)
                {
                    foreach (var d in dates)
                    {
                        var theirs = md5s[s]?[d]?["md5"]?.ToString();
                        if (theirs is null)
                        {
                            continue; // no schedule for that day (yet)
                        }

                        var path = SchedulePath(s, d);
                        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["metadata"]?["md5"]?.ToString() != theirs)
                        {
                            wanted.Add((s, d));
                        }
                    }
                }
            }

            progress?.Report(20);
            var fetchedDays = 0;
            foreach (var batch in wanted.GroupBy(w => w.Station).Chunk(StationBatch))
            {
                var body = new JsonArray(batch.Select(g => (JsonNode)new JsonObject { ["stationID"] = g.Key, ["date"] = new JsonArray(g.Select(w => (JsonNode)w.Date).ToArray()) }).ToArray());
                var reply = await _client.PostAsync(cfg, "schedules", body, ct).ConfigureAwait(false);
                foreach (var day in reply.AsArray().OfType<JsonObject>())
                {
                    // A day still being prepared (7100) comes next time.
                    if (day["metadata"]?["code"]?.GetValue<int>() is int code && code != 0)
                    {
                        continue;
                    }

                    var station = day["stationID"]?.ToString();
                    var date = day["metadata"]?["startDate"]?.ToString();
                    if (station is null || date is null)
                    {
                        continue;
                    }

                    File.WriteAllText(SchedulePath(station, date), day.ToJsonString());
                    fetchedDays++;
                }
            }

            progress?.Report(50);

            // Programme details whose fingerprint changed (or that are new).
            var needed = new HashSet<string>();
            foreach (var s in stations)
            {
                foreach (var d in dates)
                {
                    var path = SchedulePath(s, d);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    foreach (var p in JsonNode.Parse(File.ReadAllText(path))?["programs"]?.AsArray() ?? new JsonArray())
                    {
                        if (p?["programID"]?.ToString() is not { } id || needed.Contains(id))
                        {
                            continue;
                        }

                        var cached = ProgramPath(id);
                        if (!File.Exists(cached) || ProgramMd5(cached) != p["md5"]?.ToString())
                        {
                            needed.Add(id);
                        }
                        else
                        {
                            // Still in the guide: kept by the clean-up below.
                            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                        }
                    }
                }
            }

            var fetchedPrograms = 0;
            var batches = needed.Chunk(ProgramBatch).ToList();
            for (var i = 0; i < batches.Count; i++)
            {
                var reply = await _client.PostAsync(cfg, "programs", new JsonArray(batches[i].Select(id => (JsonNode)id).ToArray()), ct).ConfigureAwait(false);
                foreach (var p in reply.AsArray().OfType<JsonObject>())
                {
                    // Queued (6001) or failed (6000): asked again next time.
                    if (p["code"]?.GetValue<int>() is int code && code != 0 || p["programID"]?.ToString() is not { } id)
                    {
                        continue;
                    }

                    File.WriteAllText(ProgramPath(id), p.ToJsonString());
                    fetchedPrograms++;
                }

                progress?.Report(50 + (45.0 * (i + 1) / batches.Count));
            }

            Tidy(dates[0], stations);
            // Nothing used yet (an update just to list the lineups): no stamp, so the first real one isn't skipped.
            if (used.Count > 0)
            {
                File.WriteAllText(stamp, usedList);
            }
            sw.Stop();
            LastStatus = new UpdateStatus(DateTime.UtcNow, lineups.Count, stations.Count, fetchedDays, fetchedPrograms, sw.ElapsedMilliseconds, null, null);
            _logger.LogInformation(
                "Schedules Direct: updated {Lineups} lineups, {Stations} stations; fetched {Days} changed days and {Programs} programmes ({Ms} ms)",
                lineups.Count, stations.Count, fetchedDays, fetchedPrograms, sw.ElapsedMilliseconds);
            progress?.Report(100);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastStatus = (LastStatus ?? new UpdateStatus(null, 0, 0, 0, 0, 0, null, null)) with { Error = ex.Message, ErrorUtc = DateTime.UtcNow };
            _logger.LogWarning(ex, "Schedules Direct update failed");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Past days, stations no guide uses any more, and programmes no longer in any schedule for a week.
    private void Tidy(string today, HashSet<string> stations)
    {
        var yesterday = DateTime.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "schedules"), "*.json"))
        {
            var parts = Path.GetFileNameWithoutExtension(f).Split('_');
            var date = parts.Last();
            if (string.CompareOrdinal(date, yesterday) < 0 || !stations.Contains(parts[0]))
            {
                TryDelete(f);
            }
        }

        var stale = DateTime.UtcNow.AddDays(-7);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "programs"), "*.json"))
        {
            if (File.GetLastWriteTimeUtc(f) < stale)
            {
                TryDelete(f);
            }
        }
    }

    private static string? ProgramMd5(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?["md5"]?.ToString();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    // Lineup, station and programme ids are letters, digits and dashes; anything else is dropped.
    private static string Safe(string id) => new(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.').ToArray());
}
