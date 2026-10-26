using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyDvrGuides.SchedulesDirect;

/// <summary>
/// Dashboard › Scheduled Tasks: brings the Schedules Direct copy up to date, once a day at a
/// time picked at random for this server (2:00 to 6:00, never on the hour) and at start-up
/// if the last update is over 20 hours old. Does nothing until a guide
/// source uses Schedules Direct.
/// </summary>
public sealed class SdUpdateTask : IScheduledTask
{
    private readonly SdUpdater _updater;

    public SdUpdateTask(SdUpdater updater)
    {
        _updater = updater;
    }

    public string Name => "Update Schedules Direct guide";

    public string Key => "JellyDvrGuidesSchedulesDirect";

    public string Description => "Downloads what changed in the Schedules Direct guide (at most once a day) for JellyDVR Guides' Schedules Direct sources.";

    public string Category => "Live TV";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        var usesSd = cfg is not null && cfg.Sources.Any(s => s.Type == Configuration.GuideSource.SchedulesDirect);
        if (!usesSd || string.IsNullOrEmpty(cfg!.SdPasswordHash))
        {
            return;
        }

        await _updater.UpdateAsync(false, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A time of day for the daily update, in minutes after midnight: 2:01 to 5:59 local, never
    /// on the hour, so every server running this plugin doesn't ask Schedules Direct at once.
    /// </summary>
    internal static int PickMinuteOfDay(Random random)
    {
        var minute = random.Next(2 * 60, 6 * 60 - 1) + 1;
        return minute % 60 == 0 ? minute + 1 : minute;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromMinutes(Plugin.Instance?.Configuration.SdUpdateMinuteOfDay is >= 0 and var m ? m : 5 * 60 + 17).Ticks
        },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
    };
}
