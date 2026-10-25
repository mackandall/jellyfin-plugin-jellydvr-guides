using System.Text;
using Jellyfin.Plugin.JellyDvrGuides.Guides;
using Xunit;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

public class GuideCleanerTests
{
    private const string Guide = """
        <tv>
          <programme start="20261025010000 +0000" stop="20261025030000 +0000" channel="1">
            <title>Heat (1995)</title><date>1995</date>
            <episode-num system="tms">MV000000010000</episode-num>
          </programme>
          <programme start="20261025030000 +0000" stop="20261025050000 +0000" channel="1">
            <title>Blade Runner 2049 (2017)</title><date>20171006</date>
          </programme>
          <programme start="20261025050000 +0000" stop="20261025060000 +0000" channel="1">
            <title>1984 (1956)</title><date>1984</date>
          </programme>
        </tv>
        """;

    private static (GuideCleaner.CleanResult Result, string Xml) Clean(string xml, bool years = true, bool ids = true)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        using var output = new MemoryStream();
        var result = GuideCleaner.Clean(input, output, CancellationToken.None, years, ids);
        return (result, Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void Removes_a_year_only_when_it_matches_the_date()
    {
        var (result, xml) = Clean(Guide);
        Assert.Equal(3, result.Programmes);
        Assert.Equal(2, result.TitlesCleaned);
        Assert.Contains("<title>Heat</title>", xml);
        Assert.Contains("<title>Blade Runner 2049</title>", xml);
        Assert.Contains("<title>1984 (1956)</title>", xml);
    }

    [Fact]
    public void Copies_the_gracenote_id_to_dd_progid()
    {
        var (result, xml) = Clean(Guide);
        Assert.Equal(1, result.ProgramIdsAdded);
        Assert.Contains("system=\"dd_progid\">MV000000010000<", xml);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Each_fix_can_be_turned_off(bool years, bool ids)
    {
        var (result, xml) = Clean(Guide, years, ids);
        Assert.Equal(years ? 2 : 0, result.TitlesCleaned);
        Assert.Equal(ids ? 1 : 0, result.ProgramIdsAdded);
        Assert.Equal(years, xml.Contains("<title>Heat</title>"));
        Assert.Equal(ids, xml.Contains("dd_progid"));
        Assert.Equal(3, result.Programmes);
    }

    [Fact]
    public void An_empty_document_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => Clean(string.Empty));
    }
}
