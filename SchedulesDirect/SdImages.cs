using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyDvrGuides.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDvrGuides.SchedulesDirect;

/// <summary>
/// Programme pictures for Schedules Direct guides. The guide points Jellyfin at this plugin
/// (image/&lt;key&gt;.jpg), not at Schedules Direct: each picture is downloaded once, the first
/// time it's asked for, and kept, so the daily image cap is never spent twice on one picture.
/// One picture per show (all its episodes share it) and one per movie.
/// </summary>
public sealed partial class SdImages
{
    private readonly SdClient _client;
    private readonly SdUpdater _updater;
    private readonly ILogger<SdImages> _logger;
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perKey = new();
    private const string NoneMark = "asked with show id";
    private string? _lastError;
    private DateTime _lastErrorUtc;

    public SdImages(SdClient client, SdUpdater updater, ILogger<SdImages> logger)
    {
        _client = client;
        _updater = updater;
        _logger = logger;
    }

    private string Folder => Path.Combine(_updater.Root, "images");

    /// <summary>
    /// The picture's key for a programme: its first 10 characters, with an episode (EP) under
    /// its show's (SH, same digits), so a show's episodes share one picture.
    /// </summary>
    public static string? KeyOf(string programId) =>
        ProgramId().IsMatch(programId) ? (programId.StartsWith("EP", StringComparison.Ordinal) ? "SH" : programId[..2]) + programId.Substring(2, 8) : null;

    /// <summary>
    /// The picture for a key (asking Schedules Direct about <paramref name="programId"/> the
    /// first time), or null when there's none or it can't be fetched today.
    /// </summary>
    public async Task<string?> GetAsync(string key, string programId, CancellationToken ct)
    {
        if (!Key().IsMatch(key) || !ProgramId().IsMatch(programId) || KeyOf(programId) != key)
        {
            return null;
        }

        var path = Path.Combine(Folder, key + ".jpg");
        var none = Path.Combine(Folder, key + ".none");
        if (File.Exists(path))
        {
            return path;
        }

        // Known to have no picture: asked again after a month. Markers without the current
        // NoneMark predate asking with the show id, so they're asked again now.
        if (File.Exists(none) && File.GetLastWriteTimeUtc(none) > DateTime.UtcNow.AddDays(-30) && File.ReadAllText(none) == NoneMark)
        {
            return null;
        }

        var gate = _perKey.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            Directory.CreateDirectory(Folder);
            // A show's pictures come with its 10-character id (an episode's own id often has
            // none, news especially); movies and sports events have their own.
            var data = key.StartsWith("SH", StringComparison.Ordinal) ? await PicturesAsync(cfg, key, ct).ConfigureAwait(false) : null;
            if (data is null || data.Count == 0)
            {
                data = await PicturesAsync(cfg, programId, ct).ConfigureAwait(false);
            }

            var best = data?.OfType<JsonObject>()
                .Where(d => d["uri"] is not null)
                .OrderByDescending(d => Score(d, key.StartsWith("MV", StringComparison.Ordinal)))
                .FirstOrDefault();
            var image = best is null ? null : await _client.GetImageAsync(cfg, best["uri"]!.ToString(), ct).ConfigureAwait(false);
            if (image is null)
            {
                File.WriteAllText(none, NoneMark);
                return null;
            }

            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, image.Value.Bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (SdException ex)
        {
            // Today's cap or a lockout: no picture for now, asked again later. Logged once an
            // hour per reason: a guide refresh asks for thousands at once.
            if (ex.Message != _lastError || DateTime.UtcNow > _lastErrorUtc.AddHours(1))
            {
                _lastError = ex.Message;
                _lastErrorUtc = DateTime.UtcNow;
                _logger.LogWarning("No programme pictures from Schedules Direct for now (first: {Key}): {Message}", key, ex.Message);
            }

            return null;
        }
        finally
        {
            _downloads.Release();
            gate.Release();
        }
    }

    // The pictures Schedules Direct lists for one id (null when none: it answers with an error object).
    private async Task<JsonArray?> PicturesAsync(PluginConfiguration cfg, string id, CancellationToken ct)
    {
        var reply = await _client.PostAsync(cfg, "metadata/programs/", new JsonArray(id), ct).ConfigureAwait(false);
        return reply.AsArray().OfType<JsonObject>().FirstOrDefault()?["data"] as JsonArray;
    }

    // A movie: a poster (2x3). Anything else: the show's own picture, landscape (4x3, then
    // 16x9). Medium sizes, like the guide pictures Channels DVR gives.
    private static int Score(JsonObject d, bool movie)
    {
        var aspect = d["aspect"]?.ToString();
        var category = d["category"]?.ToString() ?? string.Empty;
        var tier = d["tier"]?.ToString();
        var width = int.TryParse(d["width"]?.ToString(), out var w) ? w : 0;
        var score = movie
            ? (aspect == "2x3" ? 100 : aspect == "3x4" ? 60 : 0)
            : (aspect == "4x3" ? 100 : aspect == "16x9" ? 80 : 0);
        score += category switch
        {
            "Poster Art" or "Box Art" => movie ? 40 : 10,
            "Banner-L1" or "Iconic" => 30,
            "Banner-L2" or "Banner" => 20,
            "Staple" or "Banner-L3" => 10,
            _ => 0
        };
        score += tier is "Series" or null ? 15 : tier == "Season" ? 8 : 0;
        // Closest to 480 (portrait) or 720 (landscape) wide.
        score -= Math.Abs(width - (movie ? 480 : 720)) / 40;
        return score;
    }

    [GeneratedRegex("^(EP|MV|SH|SP)[0-9]{12}$")]
    private static partial Regex ProgramId();

    [GeneratedRegex("^(MV|SH|SP)[0-9]{8}$")]
    private static partial Regex Key();
}
