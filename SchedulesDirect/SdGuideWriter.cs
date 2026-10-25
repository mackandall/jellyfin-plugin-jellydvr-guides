using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml;

namespace Jellyfin.Plugin.XmltvCleaner.SchedulesDirect;

/// <summary>
/// Writes one Schedules Direct lineup as an XMLTV guide, from the local copy only, shaped like
/// Channels DVR's guide (which Jellyfin already reads well): channel ids are the channel
/// numbers ("4.1", "202"), so channels map to the tuner's by number and keep the programme
/// ids Jellyfin's timers know; categories say Movie / Series / Sports; the Gracenote id goes
/// in a "tms" episode-num (the cleaner then copies it to dd_progid).
/// </summary>
public static class SdGuideWriter
{
    private const string XmltvTime = "yyyyMMddHHmmss '+0000'";

    /// <summary>Writes the guide; returns how many programmes it has. Throws when the lineup isn't in the local copy.</summary>
    public static int Write(SdUpdater cache, string lineup, int days, string imageBase, Stream output, CancellationToken ct)
    {
        var lineupPath = cache.LineupPath(lineup);
        if (!File.Exists(lineupPath))
        {
            throw new InvalidOperationException($"Lineup {lineup} isn't in the Schedules Direct copy yet: update from Schedules Direct first (or check the lineup id).");
        }

        var map = JsonNode.Parse(File.ReadAllText(lineupPath))!;
        var stations = (map["stations"]?.AsArray() ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(s => s["stationID"] is not null)
            .GroupBy(s => s["stationID"]!.ToString())
            .ToDictionary(g => g.Key, g => g.First());

        // Each channel number with its station (a station can be on more than one number).
        var channels = new List<(string Number, string Station)>();
        foreach (var e in (map["map"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>())
        {
            var station = e["stationID"]?.ToString();
            var number = e["atscMajor"] is { } major
                ? major + "." + (e["atscMinor"]?.ToString() ?? "0")
                : (e["channel"]?.ToString() ?? e["logicalChannelNumber"]?.ToString())?.TrimStart('0');
            if (station is null || string.IsNullOrEmpty(number) || channels.Any(c => c.Number == number))
            {
                continue;
            }

            channels.Add((number, station));
        }

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using var w = XmlWriter.Create(output, settings);
        w.WriteStartDocument();
        w.WriteStartElement("tv");
        w.WriteAttributeString("source-info-name", "schedulesdirect");
        foreach (var (number, station) in channels)
        {
            stations.TryGetValue(station, out var s);
            w.WriteStartElement("channel");
            w.WriteAttributeString("id", number);
            w.WriteElementString("lcn", number);
            foreach (var name in new[] { s?["callsign"]?.ToString(), s?["affiliate"]?.ToString(), s?["name"]?.ToString() }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                w.WriteElementString("display-name", name);
            }

            if (Logo(s) is { } logo)
            {
                w.WriteStartElement("icon");
                w.WriteAttributeString("src", logo);
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        var programs = new Dictionary<string, JsonObject?>();
        var count = 0;
        var dates = Enumerable.Range(-1, days + 1).Select(d => DateTime.UtcNow.Date.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
        foreach (var (number, station) in channels)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var date in dates)
            {
                var path = cache.SchedulePath(station, date);
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (var air in (JsonNode.Parse(File.ReadAllText(path))?["programs"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (air["programID"]?.ToString() is not { } id
                        || !DateTime.TryParse(air["airDateTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
                    {
                        continue;
                    }

                    if (!programs.TryGetValue(id, out var details))
                    {
                        var file = cache.ProgramPath(id);
                        details = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
                        programs[id] = details;
                    }

                    WriteProgramme(w, number, id, start, air, details, imageBase);
                    count++;
                }
            }
        }

        w.WriteEndElement();
        w.WriteEndDocument();
        return count;
    }

    private static void WriteProgramme(XmlWriter w, string channel, string id, DateTime start, JsonObject air, JsonObject? d, string imageBase)
    {
        var stop = start.AddSeconds(air["duration"]?.GetValue<int>() ?? 1800);
        w.WriteStartElement("programme");
        w.WriteAttributeString("start", start.ToString(XmltvTime, CultureInfo.InvariantCulture));
        w.WriteAttributeString("stop", stop.ToString(XmltvTime, CultureInfo.InvariantCulture));
        w.WriteAttributeString("channel", channel);

        w.WriteElementString("title", d?["titles"]?.AsArray().FirstOrDefault()?["title120"]?.ToString() ?? "To be announced");
        if (d?["episodeTitle150"]?.ToString() is { Length: > 0 } episode)
        {
            w.WriteElementString("sub-title", episode);
        }

        if (Description(d) is { } desc)
        {
            w.WriteElementString("desc", desc);
        }

        var entity = d?["entityType"]?.ToString();
        var categories = new List<string>();
        if (entity == "Movie")
        {
            categories.Add("Movie");
        }
        else if (entity == "Episode")
        {
            categories.Add("Episode");
            categories.Add("Series");
        }
        else if (entity == "Sports")
        {
            categories.Add("Sports");
        }

        if (d?["showType"]?.ToString() is { Length: > 0 } showType)
        {
            categories.Add(showType);
        }

        categories.AddRange((d?["genres"]?.AsArray() ?? new JsonArray()).Select(g => g?.ToString()).OfType<string>());
        foreach (var c in categories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            w.WriteElementString("category", c);
        }

        if (HasArtwork(d) && SdImages.KeyOf(id) is { } key)
        {
            w.WriteStartElement("icon");
            w.WriteAttributeString("src", $"{imageBase}XmltvCleaner/image/{key}.jpg?p={id}");
            w.WriteEndElement();
        }

        // A movie's release year; an episode's first airing.
        var year = d?["movie"]?["year"]?.ToString();
        var date = year is { Length: 4 } ? year + "0101"
            : d?["originalAirDate"]?.ToString() is { Length: 10 } aired ? aired.Replace("-", string.Empty, StringComparison.Ordinal)
            : null;
        if (date is not null)
        {
            w.WriteElementString("date", date);
        }

        var gracenote = (d?["metadata"]?.AsArray() ?? new JsonArray()).Select(m => m?["Gracenote"]).FirstOrDefault(g => g is not null);
        var season = gracenote?["season"]?.GetValue<int>();
        var number = gracenote?["episode"]?.GetValue<int>();
        if (number is > 0)
        {
            WriteEpisodeNum(w, "onscreen", season is > 0 ? $"S{season}E{number}" : $"E{number}");
            WriteEpisodeNum(w, "xmltv_ns", (season is > 0 ? (season - 1).ToString() : string.Empty) + "." + (number - 1) + ".");
        }

        WriteEpisodeNum(w, "tms", id);
        if (air["new"]?.GetValue<bool>() == true)
        {
            w.WriteStartElement("new");
            w.WriteEndElement();
        }
        else
        {
            w.WriteStartElement("previously-shown");
            w.WriteEndElement();
        }

        if (air["liveTapeDelay"]?.ToString() == "Live")
        {
            w.WriteStartElement("live");
            w.WriteEndElement();
        }

        if (Rating(d, air) is { } rating)
        {
            w.WriteStartElement("rating");
            w.WriteElementString("value", rating);
            w.WriteEndElement();
        }

        var directors = (d?["crew"]?.AsArray() ?? new JsonArray()).Where(c => c?["role"]?.ToString() == "Director").Select(c => c?["name"]?.ToString()).OfType<string>().Distinct().Take(2).ToList();
        var actors = (d?["cast"]?.AsArray() ?? new JsonArray())
            .OrderBy(c => int.TryParse(c?["billingOrder"]?.ToString(), out var b) ? b : 99)
            .Select(c => c?["name"]?.ToString()).OfType<string>().Distinct().Take(6).ToList();
        if (directors.Count + actors.Count > 0)
        {
            w.WriteStartElement("credits");
            directors.ForEach(n => w.WriteElementString("director", n));
            actors.ForEach(n => w.WriteElementString("actor", n));
            w.WriteEndElement();
        }

        if ((air["videoProperties"]?.AsArray() ?? new JsonArray()).Any(v => string.Equals(v?.ToString(), "hdtv", StringComparison.OrdinalIgnoreCase)))
        {
            w.WriteStartElement("video");
            w.WriteElementString("quality", "HDTV");
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteEpisodeNum(XmlWriter w, string system, string value)
    {
        w.WriteStartElement("episode-num");
        w.WriteAttributeString("system", system);
        w.WriteString(value);
        w.WriteEndElement();
    }

    private static bool HasArtwork(JsonObject? d) =>
        d is not null && new[] { "hasImageArtwork", "hasSeriesArtwork", "hasEpisodeArtwork", "hasSeasonArtwork", "hasMovieArtwork", "hasSportsArtwork" }
            .Any(k => d[k]?.GetValue<bool>() == true);

    // The longest English description there is.
    private static string? Description(JsonObject? d)
    {
        foreach (var size in new[] { "description1000", "description100" })
        {
            var list = (d?["descriptions"]?[size]?.AsArray() ?? new JsonArray()).OfType<JsonObject>().ToList();
            var pick = list.FirstOrDefault(x => x["descriptionLanguage"]?.ToString() is "en" or "en-US") ?? list.FirstOrDefault();
            if (pick?["description"]?.ToString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    // US TV or film rating first.
    private static string? Rating(JsonObject? d, JsonObject air)
    {
        var all = (d?["contentRating"]?.AsArray() ?? new JsonArray()).Concat(air["ratings"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>().ToList();
        var pick = all.FirstOrDefault(r => r["body"]?.ToString() is "USA Parental Rating" or "Motion Picture Association of America")
            ?? all.FirstOrDefault(r => r["country"]?.ToString() is null or "USA");
        return pick?["code"]?.ToString();
    }

    // A station logo from Schedules Direct's public storage (no token, not counted in the image cap).
    private static string? Logo(JsonObject? s)
    {
        var logos = (s?["stationLogo"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>().ToList();
        var pick = logos.FirstOrDefault(l => l["category"]?.ToString() == "dark") ?? logos.FirstOrDefault();
        return pick?["URL"]?.ToString() ?? s?["logo"]?["URL"]?.ToString();
    }
}
