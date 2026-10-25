using Jellyfin.Plugin.XmltvCleaner.Configuration;
using Jellyfin.Plugin.XmltvCleaner.Guides;
using Jellyfin.Plugin.XmltvCleaner.Providers;
using Jellyfin.Plugin.XmltvCleaner.SchedulesDirect;
using Xunit;

namespace Jellyfin.Plugin.XmltvCleaner.Tests;

public class SmallPartsTests
{
    [Theory]
    [InlineData("4.1", "4.1")]
    [InlineData("004-1", "4.1")]
    [InlineData("4_1", "4.1")]
    [InlineData("0202", "202")]
    [InlineData(" 202 ", "202")]
    [InlineData("10.0", "10.0")]
    [InlineData("0", "0")]
    public void Channel_numbers_compare_alike(string number, string normalized) =>
        Assert.Equal(normalized, TunerChannels.Normalize(number));

    [Theory]
    [InlineData("EP057750990498", "SH05775099")]
    [InlineData("SH057750990000", "SH05775099")]
    [InlineData("MV000184850000", "MV00018485")]
    [InlineData("SP123456780001", "SP12345678")]
    [InlineData("XX123", null)]
    [InlineData("EP0577509904", null)]
    public void A_show_and_its_episodes_share_one_picture(string programId, string? key) =>
        Assert.Equal(key, SdImages.KeyOf(programId));

    [Fact]
    public void A_copy_is_fresh_only_for_the_settings_it_was_made_from()
    {
        var path = Path.Combine(Path.GetTempPath(), "guide-test-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var source = new GuideSource { Id = "main", Type = GuideSource.SchedulesDirect, Lineup = "USA-X" };
            var numbers = new HashSet<string> { "202", "4.1" };
            Assert.False(GuideCopies.MadeFrom(path, source, numbers));

            GuideCopies.RecordMadeFrom(path, source, numbers);
            Assert.True(GuideCopies.MadeFrom(path, source, new HashSet<string> { "4.1", "202" }));
            Assert.False(GuideCopies.MadeFrom(path, source, new HashSet<string> { "202" }));
            Assert.False(GuideCopies.MadeFrom(path, source, null));
            source.Lineup = "USA-Y";
            Assert.False(GuideCopies.MadeFrom(path, source, numbers));
        }
        finally
        {
            File.Delete(path + ".src");
        }
    }
}
