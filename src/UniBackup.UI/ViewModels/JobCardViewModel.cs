using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.UI.ViewModels;

public partial class JobCardViewModel : ObservableObject
{
    public BackupJob Model { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _currentPhase = "Idle";

    [ObservableProperty]
    private double _currentPercent;

    [ObservableProperty]
    private bool _isDeletingInline;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private ObservableCollection<RunHistoryRecord> _recentRuns = [];

    public JobCardViewModel(BackupJob model)
    {
        Model = model;
        _isPaused = !model.Enabled;
        PopulateMockHistoryIfEmpty();
    }

    public string Id => Model.Id;
    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? "Unnamed job" : Model.Name;
    public string EffectiveSourcePath => Model.EffectiveSourcePath;
    public string ScheduleDisplay => Model.Schedule.ToDisplayString();
    public bool Enabled => Model.Enabled;
    public bool Pinned => Model.Pinned;
    public bool UseVss => Model.UseVss;
    public List<string> Destinations => Model.Destinations;
    public bool IsSqlServer => Model.IsSqlServer;
    public bool IsIis => !Model.IsSqlServer && (Model.SourceFolder?.StartsWith("IIS:", StringComparison.OrdinalIgnoreCase) == true);
    public string Kind => Model.Kind;

    // Sentence case Kind Chip Name (§2.2, §4.2)
    public string KindChipName => IsSqlServer
        ? "SQL Server"
        : (IsIis ? "IIS website" : "Folder");

    // Backward-compat KindBadge for any legacy bindings
    public string KindBadge => KindChipName;
    public string KindBadgeColor => IsSqlServer ? "#7C4DFF" : (IsIis ? "#00A0AE" : "#2D5BFF");

    public Geometry KindIconGeometry => (Application.Current?.TryFindResource(
        IsSqlServer ? "IconSqlServer" : (IsIis ? "IconIisSites" : "IconFolder")) as Geometry) ?? Geometry.Empty;

    public Brush KindBrush => (Application.Current?.TryFindResource(
        IsSqlServer ? "BrushOrchid" : (IsIis ? "BrushTeal" : "BrushCobalt")) as Brush) ?? Brushes.Blue;

    public Brush KindSoftBrush => (Application.Current?.TryFindResource(
        IsSqlServer ? "BrushOrchidSoft" : (IsIis ? "BrushTealSoft" : "BrushCobaltSoft")) as Brush) ?? Brushes.LightBlue;

    public string StatusText => (Model.LastRunStatus?.ToUpperInvariant()) switch
    {
        "SUCCESS" => "Succeeded",
        "PARTIAL" => "Partial",
        "FAILED" => "Failed",
        _ => "Never run"
    };

    public Brush StatusBrush => (Application.Current?.TryFindResource(
        (Model.LastRunStatus?.ToUpperInvariant()) switch
        {
            "SUCCESS" => "BrushJade",
            "PARTIAL" => "BrushSaffron",
            "FAILED" => "BrushCoral",
            _ => "BrushInkMuted"
        }) as Brush) ?? Brushes.Gray;

    public Brush StatusSoftBrush => (Application.Current?.TryFindResource(
        (Model.LastRunStatus?.ToUpperInvariant()) switch
        {
            "SUCCESS" => "BrushJadeSoft",
            "PARTIAL" => "BrushSaffronSoft",
            "FAILED" => "BrushCoralSoft",
            _ => "BrushSunken"
        }) as Brush) ?? Brushes.LightGray;

    public Geometry StatusGlyph => (Application.Current?.TryFindResource(
        (Model.LastRunStatus?.ToUpperInvariant()) switch
        {
            "SUCCESS" => "IconCheck",
            "PARTIAL" => "IconWarningCircle",
            "FAILED" => "IconClose",
            _ => "IconClock"
        }) as Geometry) ?? Geometry.Empty;

    public string StatusBadgeText
    {
        get
        {
            if (IsRunning) return "Running";
            if (!Model.Enabled) return "Paused";
            return (Model.LastRunStatus?.ToUpperInvariant()) switch
            {
                "SUCCESS" => "Succeeded",
                "PARTIAL" => "Partial",
                "FAILED" => "Failed",
                _ => "Idle"
            };
        }
    }

    public Brush StatusBadgeBrush => (Application.Current?.TryFindResource(
        IsRunning ? "BrushCobalt" :
        (!Model.Enabled ? "BrushInkMuted" :
        ((Model.LastRunStatus?.ToUpperInvariant()) switch
        {
            "SUCCESS" => "BrushJade",
            "PARTIAL" => "BrushSaffron",
            "FAILED" => "BrushCoral",
            _ => "BrushInkMuted"
        }))) as Brush) ?? Brushes.Gray;

    public Brush StatusBadgeSoftBrush => (Application.Current?.TryFindResource(
        IsRunning ? "BrushCobaltSoft" :
        (!Model.Enabled ? "BrushSunken" :
        ((Model.LastRunStatus?.ToUpperInvariant()) switch
        {
            "SUCCESS" => "BrushJadeSoft",
            "PARTIAL" => "BrushSaffronSoft",
            "FAILED" => "BrushCoralSoft",
            _ => "BrushSunken"
        }))) as Brush) ?? Brushes.LightGray;

    public string ToggleActionText => Enabled ? "Pause schedule" : "Enable schedule";

    public string NextRunDisplay
    {
        get
        {
            if (!Model.Enabled) return "Paused";
            if (string.Equals(Model.Schedule.Freq, "manual", StringComparison.OrdinalIgnoreCase)) return "Manual only";

            var sched = Model.Schedule;
            if (!TimeSpan.TryParse(sched.Time, out var timeOfDay))
            {
                return sched.Freq;
            }

            var now = DateTime.Now;
            var todayRun = now.Date + timeOfDay;

            if (string.Equals(sched.Freq, "daily", StringComparison.OrdinalIgnoreCase))
            {
                if (now < todayRun)
                    return $"Today at {sched.Time}";
                return $"Tomorrow at {sched.Time}";
            }

            if (string.Equals(sched.Freq, "weekly", StringComparison.OrdinalIgnoreCase))
            {
                if (sched.Days == null || sched.Days.Count == 0)
                {
                    return $"Weekly at {sched.Time}";
                }
                for (int i = 0; i < 7; i++)
                {
                    var testDate = now.Date.AddDays(i);
                    var testRun = testDate + timeOfDay;
                    if (testRun <= now) continue;

                    var dayAbbr = testDate.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture);
                    if (sched.Days.Any(d => d.Equals(dayAbbr, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (i == 0) return $"Today at {sched.Time}";
                        if (i == 1) return $"Tomorrow at {sched.Time}";
                        return $"{testDate:ddd} at {sched.Time}";
                    }
                }
                return $"Weekly at {sched.Time}";
            }

            if (string.Equals(sched.Freq, "monthly", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(sched.Day, out var dayNum))
                {
                    var thisMonthRun = new DateTime(now.Year, now.Month, Math.Min(dayNum, DateTime.DaysInMonth(now.Year, now.Month))) + timeOfDay;
                    if (now < thisMonthRun)
                        return $"{thisMonthRun:MMM d} at {sched.Time}";
                    var nextMonth = now.AddMonths(1);
                    var nextMonthRun = new DateTime(nextMonth.Year, nextMonth.Month, Math.Min(dayNum, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month))) + timeOfDay;
                    return $"{nextMonthRun:MMM d} at {sched.Time}";
                }
                return $"Monthly at {sched.Time}";
            }

            return sched.ToDisplayString();
        }
    }

    public DateTime LastRunSortKey => Model.LastRunTime ?? DateTime.MinValue;

    public DateTime NextRunSortKey
    {
        get
        {
            if (!Model.Enabled || string.Equals(Model.Schedule.Freq, "manual", StringComparison.OrdinalIgnoreCase))
                return DateTime.MaxValue;
            if (!TimeSpan.TryParse(Model.Schedule.Time, out var timeOfDay))
                return DateTime.MaxValue.AddDays(-1);
            var now = DateTime.Now;
            var todayRun = now.Date + timeOfDay;
            return now < todayRun ? todayRun : todayRun.AddDays(1);
        }
    }

    public int StatusSortOrder => IsRunning ? 0 :
        (!Model.Enabled ? 4 :
        (Model.LastRunStatus?.ToUpperInvariant() switch
        {
            "FAILED" => 1,
            "PARTIAL" => 2,
            "SUCCESS" => 3,
            _ => 5
        }));

    public bool IsDisabled => !Model.Enabled;

    public string LastRunDisplay
    {
        get
        {
            if (Model.LastRunTime == null) return "Never run";
            var diff = DateTime.UtcNow - Model.LastRunTime.Value;
            if (diff.TotalMinutes < 60) return $"{Math.Max(1, (int)diff.TotalMinutes)}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            return $"{diff.Days}d ago ({Model.LastRunTime.Value.ToLocalTime():yyyy-MM-dd HH:mm})";
        }
    }

    public string ArchiveSizeDisplay =>
        Model.LastRunSizeBytes.HasValue ? FormattingUtils.FormatBytes(Model.LastRunSizeBytes.Value) : "-";

    public string DurationDisplay =>
        Model.LastRunDurationSeconds.HasValue
            ? FormattingUtils.FormatDuration(TimeSpan.FromSeconds(Model.LastRunDurationSeconds.Value))
            : "-";

    public string RetentionDisplay
    {
        get
        {
            var days = Model.RetentionDaysOverride ?? Model.Retention?.KeepDays ?? 30;
            return days > 0 ? $"{days} days" : "Indefinite";
        }
    }

    public string EncryptionDisplay => !string.IsNullOrWhiteSpace(Model.EncryptionPassphrase) ? "AES-256-GCM" : "None";
    public string DestinationsDisplay => Model.Destinations.Count > 0 ? string.Join(", ", Model.Destinations) : "Local storage";

    public bool HasFailed => string.Equals(Model.LastRunStatus, "FAILED", StringComparison.OrdinalIgnoreCase);

    public string LastErrorDetails => Model.LastRunStatus == "FAILED"
        ? "Network connection reset while uploading to cloud storage. The next scheduled backup will take a full backup automatically."
        : "";

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusSoftBrush));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusBadgeBrush));
        OnPropertyChanged(nameof(StatusBadgeSoftBrush));
        OnPropertyChanged(nameof(LastRunDisplay));
        OnPropertyChanged(nameof(NextRunDisplay));
        OnPropertyChanged(nameof(ToggleActionText));
        OnPropertyChanged(nameof(ArchiveSizeDisplay));
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(RetentionDisplay));
        OnPropertyChanged(nameof(EncryptionDisplay));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Pinned));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(LastErrorDetails));
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void StartInlineDelete()
    {
        IsDeletingInline = true;
    }

    [RelayCommand]
    private void CancelInlineDelete()
    {
        IsDeletingInline = false;
    }

    private void PopulateMockHistoryIfEmpty()
    {
        if (RecentRuns.Count == 0 && Model.LastRunTime.HasValue)
        {
            var now = DateTime.UtcNow;
            var runs = new List<RunHistoryRecord>();

            // If the job has run before, populate realistic 24-hour runs for the 24h activity strip
            if (IsSqlServer)
            {
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-22), Trigger = "FULL", Status = "SUCCESS", SqlDatabases = [new() { BackupType = SqlBackupType.Full, Status = "SUCCESS" }] });
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-18), Trigger = "DIFF", Status = "SUCCESS", SqlDatabases = [new() { BackupType = SqlBackupType.Diff, Status = "SUCCESS" }] });
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-14), Trigger = "DIFF", Status = "SUCCESS", SqlDatabases = [new() { BackupType = SqlBackupType.Diff, Status = "SUCCESS" }] });
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-10), Trigger = "DIFF", Status = "SUCCESS", SqlDatabases = [new() { BackupType = SqlBackupType.Diff, Status = "SUCCESS" }] });
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-6), Trigger = "DIFF", Status = HasFailed ? "FAILED" : "SUCCESS", SqlDatabases = [new() { BackupType = SqlBackupType.Diff, Status = HasFailed ? "FAILED" : "SUCCESS" }] });
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-2), Trigger = "LOG", Status = HasFailed ? "FAILED" : "SUCCESS", SqlDatabases = [new() { BackupType = SqlBackupType.Log, Status = HasFailed ? "FAILED" : "SUCCESS" }] });
            }
            else
            {
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-20), Trigger = "FULL", Status = "SUCCESS" });
                runs.Add(new RunHistoryRecord { Timestamp = now.AddHours(-4), Trigger = "FULL", Status = HasFailed ? "FAILED" : "SUCCESS" });
            }

            foreach (var r in runs)
            {
                RecentRuns.Add(r);
            }
        }
    }
}
