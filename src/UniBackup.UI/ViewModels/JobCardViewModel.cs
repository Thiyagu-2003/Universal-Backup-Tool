using UniBackup.Core.Common;
using UniBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UniBackup.UI.ViewModels;

public partial class JobCardViewModel : ObservableObject
{
    public BackupJob Model { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    public JobCardViewModel(BackupJob model)
    {
        Model = model;
    }

    public string Id => Model.Id;
    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? "Unnamed Job" : Model.Name;
    public string EffectiveSourcePath => Model.EffectiveSourcePath;
    public string ScheduleDisplay => Model.Schedule.ToDisplayString();
    public bool Enabled => Model.Enabled;
    public bool Pinned => Model.Pinned;
    public bool UseVss => Model.UseVss;
    public List<string> Destinations => Model.Destinations;
    public bool IsSqlServer => Model.IsSqlServer;
    public string Kind => Model.Kind;

    public string KindBadge => Model.IsSqlServer
        ? "SQL SERVER"
        : (Model.SourceFolder.StartsWith("IIS:", StringComparison.OrdinalIgnoreCase) ? "IIS SITE" : "FOLDER");

    public string KindBadgeColor => Model.IsSqlServer ? "#38BDF8" : "#818CF8";

    public string StatusText => Model.LastRunStatus ?? "NEVER RUN";

    public string StatusColor => (Model.LastRunStatus?.ToUpperInvariant()) switch
    {
        "SUCCESS" => "#10B981",
        "PARTIAL" => "#F59E0B",
        "FAILED" => "#EF4444",
        _ => "#94A3B8"
    };

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

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(LastRunDisplay));
        OnPropertyChanged(nameof(ArchiveSizeDisplay));
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Pinned));
    }
}
