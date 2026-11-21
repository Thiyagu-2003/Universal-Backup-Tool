using System.Globalization;
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class ScheduleCalculator
{
    public static SqlBackupType? Due(JobSchedule schedule, JobRunState lastRuns, DateTimeOffset now, bool isSqlServer)
    {
        // 1. Evaluate Full backup
        var full = schedule.GetEffectiveFullSchedule();
        if (IsFullDue(full, schedule.MissedRunLookbackMinutes, lastRuns.LastFullTime, now))
        {
            return SqlBackupType.Full;
        }

        if (!isSqlServer)
        {
            return null;
        }

        // 2. Check Run Window for DIFF / LOG
        if (schedule.Window != null && schedule.Window.Enabled)
        {
            if (!IsInsideWindow(schedule.Window, now))
            {
                return null;
            }
        }

        // 3. Evaluate Diff backup
        if (schedule.Diff != null && schedule.Diff.Enabled && schedule.Diff.EveryMinutes > 0)
        {
            var lastDiffOrFull = lastRuns.LastDiffTime ?? lastRuns.LastFullTime;
            if (!lastDiffOrFull.HasValue || (now - lastDiffOrFull.Value).TotalMinutes >= schedule.Diff.EveryMinutes)
            {
                return SqlBackupType.Diff;
            }
        }

        // 4. Evaluate Log backup
        if (schedule.Log != null && schedule.Log.Enabled && schedule.Log.EveryMinutes > 0)
        {
            var lastLogOrBase = lastRuns.LastLogTime ?? lastRuns.LastDiffTime ?? lastRuns.LastFullTime;
            if (!lastLogOrBase.HasValue || (now - lastLogOrBase.Value).TotalMinutes >= schedule.Log.EveryMinutes)
            {
                return SqlBackupType.Log;
            }
        }

        return null;
    }

    public static bool IsFolderJobDue(JobSchedule schedule, JobRunState lastRuns, DateTimeOffset now)
    {
        var full = schedule.GetEffectiveFullSchedule();
        return IsFullDue(full, schedule.MissedRunLookbackMinutes, lastRuns.LastRunTime, now);
    }

    private static bool IsFullDue(FullBackupSchedule full, int missedRunLookbackMinutes, DateTimeOffset? lastRunTime, DateTimeOffset now)
    {
        if (full.Freq.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TimeSpan.TryParse(full.Time, CultureInfo.InvariantCulture, out var scheduledTimeOfDay))
            return false;

        bool dayMatches = full.Freq.ToLowerInvariant() switch
        {
            "daily" => true,
            "weekly" => full.Days.Any(d => d.Equals(now.ToString("ddd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)),
            "monthly" => int.TryParse(full.Day, out var d) && now.Day == d,
            "onetime" => DateTime.TryParse(full.Date, CultureInfo.InvariantCulture, out var dt) && dt.Date == now.Date,
            _ => false
        };

        if (!dayMatches)
            return false;

        var scheduledSlotToday = new DateTimeOffset(now.Year, now.Month, now.Day, scheduledTimeOfDay.Hours, scheduledTimeOfDay.Minutes, 0, now.Offset);

        if (now < scheduledSlotToday)
            return false; // not yet time today

        if (lastRunTime.HasValue && lastRunTime.Value >= scheduledSlotToday)
            return false; // already ran for this slot

        // Check if within missed-run lookback window
        var elapsedMinutes = (now - scheduledSlotToday).TotalMinutes;
        return elapsedMinutes <= missedRunLookbackMinutes;
    }

    private static bool IsInsideWindow(ScheduleRunWindow window, DateTimeOffset now)
    {
        if (window.Days.Count > 0 && !window.Days.Any(d => d.Equals(now.ToString("ddd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)))
            return false;

        if (TimeSpan.TryParse(window.Start, CultureInfo.InvariantCulture, out var start) &&
            TimeSpan.TryParse(window.End, CultureInfo.InvariantCulture, out var end))
        {
            var timeOfDay = now.TimeOfDay;
            return timeOfDay >= start && timeOfDay <= end;
        }

        return true;
    }
}
