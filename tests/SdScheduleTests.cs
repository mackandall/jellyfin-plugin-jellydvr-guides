using Jellyfin.Plugin.JellyDvrGuides.SchedulesDirect;
using Xunit;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

public class SdScheduleTests
{
    [Fact]
    public void PickMinuteOfDay_StaysBetweenTwoAndSixAndOffTheHour()
    {
        var random = new Random(1);
        var seen = new HashSet<int>();
        for (var i = 0; i < 10000; i++)
        {
            var m = SdUpdateTask.PickMinuteOfDay(random);
            Assert.InRange(m, 2 * 60 + 1, 6 * 60 - 1);
            Assert.NotEqual(0, m % 60);
            seen.Add(m);
        }

        // Spread over the whole window, not bunched.
        Assert.True(seen.Count > 200);
    }

    [Fact]
    public void MayForce_OnlyFifteenMinutesAfterTheLastUpdate()
    {
        var now = new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc);
        Assert.False(SdUpdater.MayForce(now.AddMinutes(-5), now));
        Assert.True(SdUpdater.MayForce(now.AddMinutes(-15), now));
        Assert.True(SdUpdater.MayForce(DateTime.MinValue, now));
    }
}
