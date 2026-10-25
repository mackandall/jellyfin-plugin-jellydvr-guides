using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.XmltvCleaner.SchedulesDirect;

/// <summary>
/// Dashboard › Scheduled Tasks: brings the Schedules Direct copy up to date, once a day at
/// 5:00 and at start-up if the last update is over 20 hours old. Does nothing until a guide
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

    public string Key => "XmltvCleanerSchedulesDirect";

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

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
    };
}
