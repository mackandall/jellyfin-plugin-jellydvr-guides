using Jellyfin.Plugin.JellyDvrGuides.Configuration;
using Jellyfin.Plugin.JellyDvrGuides.Providers;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

public class ProviderSyncTests
{
    private static ListingsProviderInfo Xmltv(string id, string path, params string[] tuners) =>
        new() { Id = id, Type = "xmltv", Path = path, EnabledTuners = tuners, EnableAllTuners = false };

    private static readonly ListingsProviderInfo[] Providers =
    {
        Xmltv("bh", "http://s:8096/XmltvCleaner/guide.xml", "BH"),
        Xmltv("fc", "http://s:6502/plexlive/epg.xml", "FC"),
        Xmltv("cdvr", "http://cdvr:8089/devices/HD/guide/xmltv", "HD"),
        Xmltv("ant", "http://s:8096/JellyDvrGuides/guide/antenna.xml", "HD"),
        new() { Id = "sd", Type = "schedulesdirect", ListingsId = "USA-X", EnableAllTuners = true }
    };

    [Fact]
    public void A_source_finds_its_provider_by_id_then_address_then_old_address()
    {
        Assert.Equal("bh", ProviderSync.Match(Providers, new GuideSource { Id = "main" }, 0)?.Id);
        Assert.Equal("ant", ProviderSync.Match(Providers, new GuideSource { Id = "antenna" }, 1)?.Id);
        Assert.Equal("fc", ProviderSync.Match(Providers, new GuideSource { Id = "whatever", ProviderId = "fc" }, 5)?.Id);
        Assert.Null(ProviderSync.Match(Providers, new GuideSource { Id = "other" }, 2));
    }

    [Fact]
    public void Other_providers_on_the_same_tuners_are_listed_and_the_earlier_ones_flagged()
    {
        var others = ProviderSync.Others(Providers, Providers[3], new GuideSource { Id = "antenna", Tuners = new[] { "HD" } });
        Assert.Equal(new[] { "cdvr", "sd" }, others.Select(o => o.Id));
        Assert.True(others[0].ComesFirst);
        Assert.False(others[1].ComesFirst);
        Assert.True(others[1].AllTuners);
    }

    [Fact]
    public void A_source_without_a_provider_yet_uses_its_ticked_tuners()
    {
        var others = ProviderSync.Others(Providers, null, new GuideSource { Id = "new", Tuners = new[] { "FC" } });
        Assert.Equal(new[] { "fc", "sd" }, others.Select(o => o.Id));
        Assert.All(others, o => Assert.True(o.ComesFirst));
    }

    [Fact]
    public void The_guide_address_uses_the_new_prefix()
    {
        Assert.Equal("http://s:8096/JellyDvrGuides/guide/antenna.xml", ProviderSync.AddressOf("http://s:8096/", new GuideSource { Id = "antenna" }));
    }
}
