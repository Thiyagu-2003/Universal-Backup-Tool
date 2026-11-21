using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class ScheduleCalculatorTests
{
    [Fact]
    public void Full_Due_At_Scheduled_Time_When_Not_Run_Today()
    {
        var now = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero); // 02:00 UTC
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00"
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = now.AddDays(-1) // yesterday
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Equal(SqlBackupType.Full, due);
    }

    [Fact]
    public void Full_Suppresses_SameTick_Diff_And_Log_Precedence()
    {
        var now = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00",
            Diff = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 60 },
            Log = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 15 }
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = now.AddDays(-1),
            LastDiffTime = now.AddHours(-3),
            LastLogTime = now.AddMinutes(-30)
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        // Precedence: Full must win over Diff and Log
        Assert.Equal(SqlBackupType.Full, due);
    }

    [Fact]
    public void Diff_Due_When_Interval_Elapsed_And_Full_Already_Done_Today()
    {
        var now = new DateTimeOffset(2026, 11, 21, 6, 0, 0, TimeSpan.Zero); // 06:00
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00",
            Diff = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 240 }, // every 4 hours
            Log = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 15 }
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero), // Full ran at 02:00
            LastDiffTime = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero),
            LastLogTime = now.AddMinutes(-5) // Log ran 5 min ago
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        // 4 hours elapsed since 02:00 Diff, so Diff is due
        Assert.Equal(SqlBackupType.Diff, due);
    }

    [Fact]
    public void Log_Due_Every_15_Minutes_Inside_Window()
    {
        var now = new DateTimeOffset(2026, 11, 21, 10, 15, 0, TimeSpan.Zero); // Saturday, 10:15
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00",
            Log = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 15 },
            Window = new ScheduleRunWindow
            {
                Enabled = true,
                Start = "08:00",
                End = "20:00",
                Days = ["SAT", "SUN", "MON", "TUE", "WED", "THU", "FRI"]
            }
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero),
            LastLogTime = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero) // 15 mins ago
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Equal(SqlBackupType.Log, due);
    }

    [Fact]
    public void Log_Suppressed_Outside_Run_Window()
    {
        var now = new DateTimeOffset(2026, 11, 21, 22, 0, 0, TimeSpan.Zero); // 22:00 (outside 08:00-20:00)
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00",
            Log = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 15 },
            Window = new ScheduleRunWindow
            {
                Enabled = true,
                Start = "08:00",
                End = "20:00",
                Days = ["SAT", "SUN", "MON", "TUE", "WED", "THU", "FRI"]
            }
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero),
            LastLogTime = now.AddHours(-1) // Log is overdue, but outside window
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Null(due);
    }

    [Fact]
    public void Missed_Full_Caught_Up_Within_Lookback()
    {
        // Scheduled at 02:00, server was down and booted at 02:45 (45 mins late, within 120m lookback)
        var now = new DateTimeOffset(2026, 11, 21, 2, 45, 0, TimeSpan.Zero);
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00",
            MissedRunLookbackMinutes = 120
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = now.AddDays(-1)
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Equal(SqlBackupType.Full, due);
    }

    [Fact]
    public void Missed_Full_Ignored_Beyond_Lookback()
    {
        // Scheduled at 02:00, current time is 06:00 (4 hours late, exceeds 120m lookback)
        var now = new DateTimeOffset(2026, 11, 21, 6, 0, 0, TimeSpan.Zero);
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00",
            MissedRunLookbackMinutes = 120
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = now.AddDays(-1)
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Null(due);
    }

    [Fact]
    public void No_Double_Run_When_Full_Already_Completed_For_Scheduled_Slot()
    {
        var now = new DateTimeOffset(2026, 11, 21, 2, 0, 30, TimeSpan.Zero); // 30 seconds after slot
        var schedule = new JobSchedule
        {
            Freq = "daily",
            Time = "02:00"
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = new DateTimeOffset(2026, 11, 21, 2, 0, 5, TimeSpan.Zero) // completed 25s ago
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Null(due);
    }

    [Fact]
    public void Legacy_Schedule_Without_V2_Fields_Functions_Correctly()
    {
        var now = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);
        var schedule = new JobSchedule
        {
            Freq = "weekly",
            Time = "02:00",
            Days = ["SAT"] // 2026-11-21 is Saturday
        };
        var state = new JobRunState
        {
            JobId = "job1",
            LastFullTime = now.AddDays(-7)
        };

        var due = ScheduleCalculator.Due(schedule, state, now, isSqlServer: true);

        Assert.Equal(SqlBackupType.Full, due);
    }
}
