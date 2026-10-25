using Jellyfin.Plugin.JellyDvrGuides.Configuration;
using Jellyfin.Plugin.JellyDvrGuides.Web;
using Xunit;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

public class MigrationTests
{
    [Fact]
    public void The_one_guide_url_becomes_main()
    {
        var cfg = new PluginConfiguration { UpstreamUrl = " http://cdvr:8089/guide " };
        Assert.True(Plugin.MoveToSources(cfg));
        var s = Assert.Single(cfg.Sources);
        Assert.Equal(("main", "Main guide", GuideSource.Xmltv, "http://cdvr:8089/guide", true, false), (s.Id, s.Name, s.Type, s.Url, s.Clean, s.ManageProvider));
        Assert.Equal(string.Empty, cfg.UpstreamUrl);
    }

    [Fact]
    public void More_guides_and_sd_lineups_become_their_own_sources()
    {
        var cfg = new PluginConfiguration { UpstreamUrl = "http://a", ExtraUpstreamUrls = new[] { "sd:USA-OTA-73071", "http://b" }, SdDays = 10 };
        Assert.True(Plugin.MoveToSources(cfg));
        Assert.Equal(new[] { "main", "guide2", "guide3" }, cfg.Sources.Select(s => s.Id));
        var sd = cfg.Sources[1];
        Assert.Equal((GuideSource.SchedulesDirect, "USA-OTA-73071", 10, false, ""), (sd.Type, sd.Lineup, sd.Days, sd.Clean, sd.Url));
        Assert.Empty(cfg.ExtraUpstreamUrls);
    }

    [Fact]
    public void Nothing_moves_once_there_are_sources()
    {
        var cfg = new PluginConfiguration { UpstreamUrl = "http://a", Sources = new[] { new GuideSource { Id = "x" } } };
        Assert.False(Plugin.MoveToSources(cfg));
        Assert.Equal("x", Assert.Single(cfg.Sources).Id);
        Assert.False(Plugin.MoveToSources(new PluginConfiguration()));
    }

    [Fact]
    public void Old_addresses_find_the_source_named_after_them()
    {
        // Only "More guides" in 1.1: sources guide2 and guide3, no main.
        var cfg = new PluginConfiguration { ExtraUpstreamUrls = new[] { "http://b", "http://c" } };
        Plugin.MoveToSources(cfg);
        Assert.Equal(new[] { "guide2", "guide3" }, cfg.Sources.Select(s => s.Id));
        Assert.Equal("guide2", GuideController.LegacySource(cfg, 2)?.Id);
        Assert.Equal("guide3", GuideController.LegacySource(cfg, 3)?.Id);
        Assert.Equal("guide2", GuideController.LegacySource(cfg, 1)?.Id);
    }
}
