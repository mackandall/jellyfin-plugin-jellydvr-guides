using System.Text;
using Jellyfin.Plugin.JellyDvrGuides.SchedulesDirect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

public sealed class SdGuideWriterTests : IDisposable
{
    private const string Lineup = "USA-OTA-00000";
    private readonly TestPaths _paths = new();
    private readonly SdUpdater _cache;

    public SdGuideWriterTests()
    {
        _cache = new SdUpdater(new SdClient(NullLogger<SdClient>.Instance), _paths.Paths, NullLogger<SdUpdater>.Instance);
        foreach (var dir in new[] { "lineups", "schedules", "programs" })
        {
            Directory.CreateDirectory(Path.Combine(_cache.Root, dir));
        }

        File.WriteAllText(_cache.LineupPath(Lineup), """
            {"map":[{"stationID":"100","atscMajor":4,"atscMinor":1},{"stationID":"200","channel":"0202"},{"stationID":"300","channel":"0300"}],
             "stations":[{"stationID":"100","callsign":"KFORDT","affiliate":"NBC","stationLogo":[{"URL":"https://logo.example/k.png","category":"dark"}]},
                         {"stationID":"200","callsign":"CNNHD","name":"CNN HD"},{"stationID":"300","callsign":"OTHER"}]}
            """);
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        File.WriteAllText(_cache.SchedulePath("100", today), $$"""
            {"programs":[{"programID":"MV000184850000","airDateTime":"{{today}}T01:00:00Z","duration":7200},
                         {"programID":"EP057750990498","airDateTime":"{{today}}T03:00:00Z","duration":1800,"new":true,"liveTapeDelay":"Live"}]}
            """);
        File.WriteAllText(_cache.SchedulePath("200", today), $$"""
            {"programs":[{"programID":"SP123456780001","airDateTime":"{{today}}T04:00:00Z","duration":10800}]}
            """);
        File.WriteAllText(_cache.ProgramPath("MV000184850000"), """
            {"titles":[{"title120":"Ghostbusters"}],"entityType":"Movie","movie":{"year":"1984"},"hasImageArtwork":true,
             "contentRating":[{"body":"Motion Picture Association of America","code":"PG"}]}
            """);
        File.WriteAllText(_cache.ProgramPath("EP057750990498"), """
            {"titles":[{"title120":"NBC Nightly News"}],"episodeTitle150":"Oct 25","entityType":"Episode","hasSeriesArtwork":true,
             "metadata":[{"Gracenote":{"season":2,"episode":277}}]}
            """);
        File.WriteAllText(_cache.ProgramPath("SP123456780001"), """{"titles":[{"title120":"NFL Football"}],"entityType":"Sports"}""");
    }

    public void Dispose() => _paths.Dispose();

    private (int Programmes, int Channels, int InLineup, string Xml) Write(ISet<string>? only = null)
    {
        using var output = new MemoryStream();
        var r = SdGuideWriter.Write(_cache, Lineup, 14, "http://server:8096/", output, CancellationToken.None, only);
        return (r.Programmes, r.Channels, r.InLineup, Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void Channels_are_numbered_like_the_tuner()
    {
        var (programmes, channels, inLineup, xml) = Write();
        Assert.Equal((3, 3, 3), (programmes, channels, inLineup));
        Assert.Contains("<channel id=\"4.1\">", xml);
        Assert.Contains("<channel id=\"202\">", xml);
        Assert.Contains("<icon src=\"https://logo.example/k.png\"", xml);
    }

    [Fact]
    public void Programmes_carry_what_jellyfin_reads()
    {
        var xml = Write().Xml;
        Assert.Contains("system=\"dd_progid\">EP057750990498<", xml);
        Assert.Contains("system=\"onscreen\">S2E277<", xml);
        Assert.Contains("<category>Movie</category>", xml);
        Assert.Contains("<category>Series</category>", xml);
        Assert.Contains("<category>Sports</category>", xml);
        Assert.Contains("<date>19840101</date>", xml);
        Assert.Contains("<live />", xml);
        Assert.Contains("<value>PG</value>", xml);
    }

    [Fact]
    public void Pictures_point_at_the_plugin_one_per_show()
    {
        var xml = Write().Xml;
        Assert.Contains("http://server:8096/JellyDvrGuides/image/MV00018485.jpg?p=MV000184850000", xml);
        Assert.Contains("http://server:8096/JellyDvrGuides/image/SH05775099.jpg?p=EP057750990498", xml);
        Assert.DoesNotContain("image/SP12345678", xml);
    }

    [Fact]
    public void Only_the_tuners_channels_when_asked()
    {
        var (programmes, channels, inLineup, xml) = Write(new HashSet<string> { "4.1" });
        Assert.Equal((2, 1, 3), (programmes, channels, inLineup));
        Assert.DoesNotContain("<channel id=\"202\">", xml);
    }

    [Fact]
    public void A_lineup_not_downloaded_is_an_error()
    {
        using var output = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => SdGuideWriter.Write(_cache, "USA-NOPE-X", 14, "http://s/", output, CancellationToken.None));
    }
}
