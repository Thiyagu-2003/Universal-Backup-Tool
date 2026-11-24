using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.VisualElements;
using Serilog;
using SkiaSharp;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Storage;
using UniBackup.Infrastructure.Windows;
using UniBackup.UI.Services;

namespace UniBackup.UI.ViewModels;

// =============================================================================
// SUB-VIEWMODELS FOR ACTIVITY ROW & BREAKDOWNS
// =============================================================================

public partial class ActivityRowVm : ObservableObject
{
    public string RunId { get; set; } = "";
    public string JobId { get; set; } = "";
    public string JobName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string TimeAgoDisplay => GetTimeAgo(Timestamp);
    public string Kind { get; set; } = "folder";
    public string BackupTypeDisplay { get; set; } = "Folder";
    public string Status { get; set; } = "SUCCESS";
    public string StatusDisplay => Status.ToUpperInvariant();
    public string Trigger { get; set; } = "Schedule";
    public long ArchiveSizeBytes { get; set; }
    public string SizeDisplay => FormatBytes(ArchiveSizeBytes);
    public double DurationSeconds { get; set; }
    public string DurationDisplay => DurationSeconds < 60 ? $"{DurationSeconds:F1}s" : $"{Math.Floor(DurationSeconds / 60)}m {DurationSeconds % 60:F0}s";
    public string ArchiveName { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string? LogFile { get; set; }
    public List<DestinationRunResult> Destinations { get; set; } = [];
    public List<SqlDatabaseRunResult> SqlDatabases { get; set; } = [];
    public RunHistoryRecord RawRecord { get; set; } = null!;

    public string DestinationsSummary => Destinations.Count > 0
        ? string.Join(", ", Destinations.Select(d => d.Destination.ToUpperInvariant()))
        : "None";

    public Brush StatusBrush => Status.ToUpperInvariant() switch
    {
        "SUCCESS" => Application.Current?.TryFindResource("BrushJade") as Brush ?? Brushes.Green,
        "FAILED" => Application.Current?.TryFindResource("BrushCoral") as Brush ?? Brushes.Red,
        "PARTIAL" => Application.Current?.TryFindResource("BrushSaffron") as Brush ?? Brushes.Orange,
        _ => Application.Current?.TryFindResource("BrushInkMuted") as Brush ?? Brushes.Gray
    };

    public Brush StatusSoftBrush => Status.ToUpperInvariant() switch
    {
        "SUCCESS" => Application.Current?.TryFindResource("BrushJadeSoft") as Brush ?? Brushes.LightGreen,
        "FAILED" => Application.Current?.TryFindResource("BrushCoralSoft") as Brush ?? Brushes.Pink,
        "PARTIAL" => Application.Current?.TryFindResource("BrushSaffronSoft") as Brush ?? Brushes.LightYellow,
        _ => Application.Current?.TryFindResource("BrushSunken") as Brush ?? Brushes.LightGray
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string GetTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt.ToUniversalTime();
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}

public class BackupTypeSliceVm
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public double Percentage { get; set; }
    public string PercentageDisplay => $"{Percentage:F1}%";
    public string ColorHex { get; set; } = "#2563EB";
    public Brush ColorBrush => (Brush?)new BrushConverter().ConvertFrom(ColorHex) ?? Brushes.Blue;
}

public class SystemHealthVm
{
    public string EngineStatus { get; set; } = "Running";
    public Brush EngineStatusBrush { get; set; } = Brushes.Green;
    public string SchedulerStatus { get; set; } = "Active";
    public string SchedulerNextRun { get; set; } = "Schedule active";
    public string StorageConnectivity { get; set; } = "All Connected";
    public Brush StorageStatusBrush { get; set; } = Brushes.Green;
    public string LastSuccessfulBackup { get; set; } = "Never";
    public int PendingJobsCount { get; set; }
    public int FailedJobsCount { get; set; }
    public string HealthHeadline { get; set; } = "All systems operational";
}

// =============================================================================
// MAIN ANALYTICS VIEWMODEL
// =============================================================================

public partial class AnalyticsViewModel : ObservableObject
{
    private readonly ConfigurationService _configService;
    private readonly Func<AppConfig> _getConfig;
    private readonly BackupPipeline? _pipeline;
    private readonly ISchedulerService? _schedulerService;
    private readonly ServerDiagnosticsService? _diagnosticsService;
    private readonly Action<string>? _onNavigateToLogs;
    private readonly Action<string>? _onNavigateToJob;

    public AnalyticsViewModel(
        ConfigurationService configService,
        Func<AppConfig> getConfig,
        BackupPipeline? pipeline = null,
        ISchedulerService? schedulerService = null,
        ServerDiagnosticsService? diagnosticsService = null,
        Action<string>? onNavigateToLogs = null,
        Action<string>? onNavigateToJob = null)
    {
        _configService = configService;
        _getConfig = getConfig;
        _pipeline = pipeline;
        _schedulerService = schedulerService;
        _diagnosticsService = diagnosticsService;
        _onNavigateToLogs = onNavigateToLogs;
        _onNavigateToJob = onNavigateToJob;

        // Default to Last 30 days
        _selectedDatePreset = "Last30Days";
        SetPresetDates("Last30Days");
    }

    // -------------------------------------------------------------------------
    // 1. DATE-RANGE FILTER (§2)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    private string _selectedDatePreset = "Last30Days"; // Today, Last7Days, Last30Days, Last90Days, Custom

    [ObservableProperty]
    private DateTime _startDate = DateTime.Today.AddDays(-30);

    [ObservableProperty]
    private DateTime _endDate = DateTime.Today.AddDays(1).AddSeconds(-1);

    [ObservableProperty]
    private bool _isCustomDateRange;

    [ObservableProperty]
    private string _dateRangeDisplay = "";

    public bool IsPresetToday => SelectedDatePreset == "Today";
    public bool IsPreset7Days => SelectedDatePreset == "Last7Days";
    public bool IsPreset30Days => SelectedDatePreset == "Last30Days";
    public bool IsPreset90Days => SelectedDatePreset == "Last90Days";
    public bool IsPresetCustom => SelectedDatePreset == "Custom";

    partial void OnSelectedDatePresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsPresetToday));
        OnPropertyChanged(nameof(IsPreset7Days));
        OnPropertyChanged(nameof(IsPreset30Days));
        OnPropertyChanged(nameof(IsPreset90Days));
        OnPropertyChanged(nameof(IsPresetCustom));
        IsCustomDateRange = value == "Custom";

        if (value != "Custom")
        {
            SetPresetDates(value);
            _ = RefreshAsync();
        }
    }

    private void SetPresetDates(string preset)
    {
        var now = DateTime.UtcNow;
        var today = DateTime.Today;

        switch (preset)
        {
            case "Today":
                StartDate = today;
                EndDate = today.AddDays(1).AddSeconds(-1);
                DateRangeDisplay = $"Today ({today:MMM dd, yyyy})";
                break;
            case "Last7Days":
                StartDate = today.AddDays(-7);
                EndDate = today.AddDays(1).AddSeconds(-1);
                DateRangeDisplay = $"{StartDate:MMM dd} – {today:MMM dd, yyyy} (7 days)";
                break;
            case "Last90Days":
                StartDate = today.AddDays(-90);
                EndDate = today.AddDays(1).AddSeconds(-1);
                DateRangeDisplay = $"{StartDate:MMM dd} – {today:MMM dd, yyyy} (90 days)";
                break;
            case "Last30Days":
            default:
                StartDate = today.AddDays(-30);
                EndDate = today.AddDays(1).AddSeconds(-1);
                DateRangeDisplay = $"{StartDate:MMM dd} – {today:MMM dd, yyyy} (30 days)";
                break;
        }
    }

    [RelayCommand]
    public void SelectPreset(string preset)
    {
        SelectedDatePreset = preset;
    }

    [RelayCommand]
    public async Task ApplyCustomDateRangeAsync()
    {
        if (StartDate > EndDate)
        {
            var swap = StartDate;
            StartDate = EndDate;
            EndDate = swap;
        }
        DateRangeDisplay = $"{StartDate:MMM dd, yyyy} – {EndDate:MMM dd, yyyy}";
        await RefreshAsync();
    }

    // -------------------------------------------------------------------------
    // 2. FOUR KPI CARDS (§2)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    private int _totalBackupsCount;

    [ObservableProperty]
    private string _totalBackupsDeltaDisplay = "0% vs prev period";

    [ObservableProperty]
    private bool _isTotalDeltaPositive = true;

    [ObservableProperty]
    private int _successfulCount;

    [ObservableProperty]
    private double _successRate = 100.0;

    [ObservableProperty]
    private string _successRateDisplay = "100%";

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _partialCount;

    [ObservableProperty]
    private double _failureRate;

    [ObservableProperty]
    private string _failureRateDisplay = "0%";

    [ObservableProperty]
    private bool _hasFailures;

    [ObservableProperty]
    private long _totalDataBytes;

    [ObservableProperty]
    private string _totalDataDisplay = "0 B";

    [ObservableProperty]
    private string _avgRunSizeDisplay = "0 B";

    // -------------------------------------------------------------------------
    // 3. INTERACTIVE CHARTS (§2)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    private ISeries[] _activitySeries = [];

    [ObservableProperty]
    private Axis[] _activityXAxes = [];

    [ObservableProperty]
    private Axis[] _activityYAxes = [];

    [ObservableProperty]
    private ISeries[] _backupTypeSeries = [];

    public ObservableCollection<BackupTypeSliceVm> BackupTypeSlices { get; } = [];

    [ObservableProperty]
    private int _totalTypeCount;

    // -------------------------------------------------------------------------
    // 4. RECENT ACTIVITY TABLE & PAGINATION (§2)
    // -------------------------------------------------------------------------
    private List<ActivityRowVm> _allActivityRows = [];

    public ObservableCollection<ActivityRowVm> FilteredActivityRows { get; } = [];

    public ObservableCollection<ActivityRowVm> PagedActivityRows { get; } = [];

    [ObservableProperty]
    private string _searchQuery = "";

    partial void OnSearchQueryChanged(string value) => ApplyFilterAndPagination();

    [ObservableProperty]
    private string _statusFilter = "All"; // All, SUCCESS, FAILED, PARTIAL

    partial void OnStatusFilterChanged(string value) => ApplyFilterAndPagination();

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 15;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _paginationSummary = "Showing 0 of 0 backups";

    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < TotalPages;

    [RelayCommand]
    public void SetStatusFilter(string filter)
    {
        StatusFilter = filter;
    }

    [RelayCommand]
    public void NextPage()
    {
        if (CanGoNext)
        {
            CurrentPage++;
            UpdatePagedRows();
        }
    }

    [RelayCommand]
    public void PrevPage()
    {
        if (CanGoPrev)
        {
            CurrentPage--;
            UpdatePagedRows();
        }
    }

    [RelayCommand]
    public void FirstPage()
    {
        CurrentPage = 1;
        UpdatePagedRows();
    }

    [RelayCommand]
    public void LastPage()
    {
        CurrentPage = TotalPages;
        UpdatePagedRows();
    }

    // -------------------------------------------------------------------------
    // 5. EXECUTION DETAILS MODAL (§2)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    private ActivityRowVm? _selectedRow;

    [ObservableProperty]
    private bool _isDetailsOpen;

    partial void OnSelectedRowChanged(ActivityRowVm? value)
    {
        if (value != null)
        {
            IsDetailsOpen = true;
        }
    }

    [RelayCommand]
    public void CloseDetails()
    {
        IsDetailsOpen = false;
        SelectedRow = null;
    }

    [RelayCommand]
    public void ViewDetails(ActivityRowVm? row)
    {
        if (row == null) return;
        SelectedRow = row;
        IsDetailsOpen = true;
    }

    [RelayCommand]
    public void NavigateToLogs(ActivityRowVm? row)
    {
        if (row == null) return;
        IsDetailsOpen = false;
        _onNavigateToLogs?.Invoke(row.JobName);
    }

    [RelayCommand]
    public void NavigateToJob(ActivityRowVm? row)
    {
        if (row == null) return;
        IsDetailsOpen = false;
        _onNavigateToJob?.Invoke(row.JobName);
    }

    // -------------------------------------------------------------------------
    // 6. SYSTEM HEALTH (§2)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    private SystemHealthVm _systemHealth = new();

    // -------------------------------------------------------------------------
    // 7. DATA REFRESH & CALCULATION ENGINE
    // -------------------------------------------------------------------------
    [ObservableProperty]
    private bool _isLoading;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var config = _getConfig();

            // Load records for the current range plus comparison window
            var rangeDays = Math.Max(1, (int)(EndDate - StartDate).TotalDays);
            var prevRangeStart = StartDate.AddDays(-rangeDays);

            var allRuns = await _configService.LoadHistoryAsync(limit: 5000);

            // Filter current range
            var currentRuns = allRuns
                .Where(r => r.Timestamp >= StartDate.ToUniversalTime() && r.Timestamp <= EndDate.ToUniversalTime())
                .OrderByDescending(r => r.Timestamp)
                .ToList();

            // Filter previous range for comparison
            var prevRuns = allRuns
                .Where(r => r.Timestamp >= prevRangeStart.ToUniversalTime() && r.Timestamp < StartDate.ToUniversalTime())
                .ToList();

            // Compute KPIs
            ComputeKpis(currentRuns, prevRuns);

            // Build Charts
            BuildActivityChart(currentRuns);
            BuildTypeDonutChart(currentRuns);

            // Populate Table
            _allActivityRows = currentRuns.Select(MapToRowVm).ToList();
            ApplyFilterAndPagination();

            // Compute Health
            await ComputeSystemHealthAsync(config, allRuns);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh analytics dashboard: {Message}", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ComputeKpis(List<RunHistoryRecord> current, List<RunHistoryRecord> prev)
    {
        TotalBackupsCount = current.Count;

        // Delta vs previous period
        if (prev.Count > 0)
        {
            var delta = ((double)(current.Count - prev.Count) / prev.Count) * 100.0;
            TotalBackupsDeltaPercent = delta;
            TotalBackupsDeltaDisplay = $"{ComputeDelta(current.Count, prev.Count)} vs previous period";
            IsTotalDeltaPositive = delta >= 0;
        }
        else
        {
            TotalBackupsDeltaPercent = null;
            TotalBackupsDeltaDisplay = current.Count > 0 ? "First tracked period" : "No prior runs";
            IsTotalDeltaPositive = true;
        }

        // Success / Failure
        SuccessfulCount = current.Count(r => r.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase));
        PartialCount = current.Count(r => r.Status.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase));
        FailedCount = current.Count(r => r.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase));

        if (current.Count > 0)
        {
            SuccessRate = ((double)SuccessfulCount / current.Count) * 100.0;
            SuccessRateDisplay = $"{SuccessRate:F1}%";

            FailureRate = ((double)(FailedCount + PartialCount) / current.Count) * 100.0;
            FailureRateDisplay = $"{FailureRate:F1}%";
        }
        else
        {
            SuccessRate = 100.0;
            SuccessRateDisplay = "100%";
            FailureRate = 0.0;
            FailureRateDisplay = "0%";
        }

        HasFailures = (FailedCount + PartialCount) > 0;

        // Total Data
        TotalDataBytes = current.Sum(r => r.ArchiveSizeBytes);
        TotalDataDisplay = FormatBytes(TotalDataBytes);

        var avg = current.Count > 0 ? TotalDataBytes / current.Count : 0;
        AvgRunSizeDisplay = $"{FormatBytes(avg)} avg / run";
    }

    public double? TotalBackupsDeltaPercent { get; private set; }

    public static string ComputeDelta(int current, int prev)
    {
        if (prev > 0)
        {
            var delta = ((double)(current - prev) / prev) * 100.0;
            return delta >= 0 ? $"+{delta:F1}%" : $"{delta:F1}%";
        }
        if (current > 0)
            return "+100.0%";
        return "0.0%";
    }

    private void BuildActivityChart(List<RunHistoryRecord> runs)
    {
        var days = Math.Max(1, (int)(EndDate.Date - StartDate.Date).TotalDays + 1);
        var dateLabels = new List<string>();
        var successVals = new List<double>();
        var failVals = new List<double>();

        var runsByDate = runs
            .GroupBy(r => r.Timestamp.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        for (int i = 0; i < days; i++)
        {
            var day = StartDate.Date.AddDays(i);
            dateLabels.Add(day.ToString("MM/dd"));

            if (runsByDate.TryGetValue(day, out var dayRuns))
            {
                successVals.Add(dayRuns.Count(r => r.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)));
                failVals.Add(dayRuns.Count(r => !r.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                successVals.Add(0);
                failVals.Add(0);
            }
        }

        ActivitySeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Name = "Successful",
                Values = successVals,
                Stroke = new SolidColorPaint(new SKColor(0x10, 0x7C, 0x41), 2.5f),
                Fill = new SolidColorPaint(new SKColor(0x10, 0x7C, 0x41, 0x22)),
                GeometrySize = 5,
                GeometryStroke = new SolidColorPaint(new SKColor(0x10, 0x7C, 0x41), 2f),
                GeometryFill = new SolidColorPaint(SKColors.White)
            },
            new LineSeries<double>
            {
                Name = "Failed / Partial",
                Values = failVals,
                Stroke = new SolidColorPaint(new SKColor(0xDA, 0x3B, 0x01), 2.5f),
                Fill = new SolidColorPaint(new SKColor(0xDA, 0x3B, 0x01, 0x22)),
                GeometrySize = 5,
                GeometryStroke = new SolidColorPaint(new SKColor(0xDA, 0x3B, 0x01), 2f),
                GeometryFill = new SolidColorPaint(SKColors.White)
            }
        };

        ActivityXAxes = new Axis[]
        {
            new Axis
            {
                Labels = dateLabels,
                LabelsPaint = new SolidColorPaint(new SKColor(0x64, 0x74, 0x8B)),
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xE2, 0xE8, 0xF0)) { StrokeThickness = 1 }
            }
        };

        ActivityYAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(0x64, 0x74, 0x8B)),
                TextSize = 11,
                MinLimit = 0,
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xE2, 0xE8, 0xF0)) { StrokeThickness = 1 }
            }
        };
    }

    private void BuildTypeDonutChart(List<RunHistoryRecord> runs)
    {
        int fullCount = 0;
        int diffCount = 0;
        int logCount = 0;
        int folderCount = 0;
        int iisCount = 0;

        foreach (var r in runs)
        {
            var k = (r.Kind ?? "").ToLowerInvariant();
            var src = (r.SourceFolder ?? "").ToLowerInvariant();

            if (k == "sqlserver" || k == "sql" || src.StartsWith("sql:"))
            {
                if (r.SqlDatabases.Count > 0)
                {
                    var bType = r.SqlDatabases[0].BackupType;
                    if (bType == SqlBackupType.Full) fullCount++;
                    else if (bType == SqlBackupType.Diff) diffCount++;
                    else logCount++;
                }
                else if (src.Contains(":diff"))
                {
                    diffCount++;
                }
                else if (src.Contains(":log"))
                {
                    logCount++;
                }
                else
                {
                    fullCount++;
                }
            }
            else if (k == "iis" || src.StartsWith("iis:"))
            {
                iisCount++;
            }
            else
            {
                folderCount++;
            }
        }

        TotalTypeCount = runs.Count;
        var total = Math.Max(1, TotalTypeCount);

        BackupTypeSlices.Clear();

        var slices = new List<BackupTypeSliceVm>
        {
            new() { Name = "Full Backup", Count = fullCount, Percentage = ((double)fullCount / total) * 100.0, ColorHex = "#2563EB" },
            new() { Name = "Differential", Count = diffCount, Percentage = ((double)diffCount / total) * 100.0, ColorHex = "#0284C7" },
            new() { Name = "Transaction Log", Count = logCount, Percentage = ((double)logCount / total) * 100.0, ColorHex = "#059669" },
            new() { Name = "Files & Folders", Count = folderCount, Percentage = ((double)folderCount / total) * 100.0, ColorHex = "#7C3AED" },
            new() { Name = "IIS Sites", Count = iisCount, Percentage = ((double)iisCount / total) * 100.0, ColorHex = "#EA580C" }
        };

        foreach (var s in slices.Where(s => s.Count > 0))
        {
            BackupTypeSlices.Add(s);
        }

        BackupTypeSeries = new ISeries[]
        {
            new PieSeries<int> { Name = "Full", Values = new[] { fullCount }, Fill = new SolidColorPaint(new SKColor(0x25, 0x63, 0xEB)), InnerRadius = 55 },
            new PieSeries<int> { Name = "Diff", Values = new[] { diffCount }, Fill = new SolidColorPaint(new SKColor(0x02, 0x84, 0xC7)), InnerRadius = 55 },
            new PieSeries<int> { Name = "Log", Values = new[] { logCount }, Fill = new SolidColorPaint(new SKColor(0x05, 0x96, 0x69)), InnerRadius = 55 },
            new PieSeries<int> { Name = "Folder", Values = new[] { folderCount }, Fill = new SolidColorPaint(new SKColor(0x7C, 0x3A, 0xED)), InnerRadius = 55 },
            new PieSeries<int> { Name = "IIS", Values = new[] { iisCount }, Fill = new SolidColorPaint(new SKColor(0xEA, 0x58, 0x0C)), InnerRadius = 55 }
        };
    }

    private void ApplyFilterAndPagination()
    {
        var q = SearchQuery?.Trim();
        var status = StatusFilter?.ToUpperInvariant() ?? "ALL";

        var filtered = _allActivityRows.Where(r =>
        {
            if (status != "ALL" && !r.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(q))
            {
                bool matches = r.JobName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               r.BackupTypeDisplay.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               r.Status.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               r.ArchiveName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               (r.ErrorMessage?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
                if (!matches) return false;
            }

            return true;
        }).ToList();

        FilteredActivityRows.Clear();
        foreach (var r in filtered)
        {
            FilteredActivityRows.Add(r);
        }

        TotalPages = Math.Max(1, (int)Math.Ceiling((double)filtered.Count / PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        UpdatePagedRows();
    }

    private void UpdatePagedRows()
    {
        PagedActivityRows.Clear();

        var skip = (CurrentPage - 1) * PageSize;
        var paged = FilteredActivityRows.Skip(skip).Take(PageSize).ToList();

        foreach (var r in paged)
        {
            PagedActivityRows.Add(r);
        }

        var start = FilteredActivityRows.Count == 0 ? 0 : skip + 1;
        var end = Math.Min(skip + PageSize, FilteredActivityRows.Count);
        PaginationSummary = $"Showing {start}–{end} of {FilteredActivityRows.Count} backups (Page {CurrentPage} of {TotalPages})";

        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private async Task ComputeSystemHealthAsync(AppConfig config, List<RunHistoryRecord> allRuns)
    {
        var health = new SystemHealthVm();

        // 1. Windows Service Status
        var svcStatus = WindowsServiceManager.GetServiceStatus();
        health.EngineStatus = svcStatus == "Running" ? "Service Active" : "Standalone Engine";
        health.EngineStatusBrush = svcStatus == "Running"
            ? (Application.Current?.TryFindResource("BrushJade") as Brush ?? Brushes.Green)
            : (Application.Current?.TryFindResource("BrushCobalt") as Brush ?? Brushes.Blue);

        // 2. Scheduler
        if (_schedulerService != null)
        {
            var taskInfo = _schedulerService.QueryAllJobsTaskInfo();
            health.SchedulerStatus = taskInfo.Installed ? $"Task Scheduler ({taskInfo.State})" : "Manual / Standalone";
            health.SchedulerNextRun = taskInfo.Running ? "Running now" : (string.IsNullOrEmpty(taskInfo.Detail) ? "Scheduled" : taskInfo.Detail);
        }

        // 3. Cloud Storage Connectivity
        var configuredSources = RecoveryViewModel.GetConfiguredSources(config);
        health.StorageConnectivity = configuredSources.Count > 0
            ? $"{configuredSources.Count} destination(s) active"
            : "No storage targets active";
        health.StorageStatusBrush = configuredSources.Count > 0
            ? (Application.Current?.TryFindResource("BrushJade") as Brush ?? Brushes.Green)
            : (Application.Current?.TryFindResource("BrushCoral") as Brush ?? Brushes.Orange);

        // 4. Last Successful Backup
        var lastSuccess = allRuns.FirstOrDefault(r => r.Status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase));
        if (lastSuccess != null)
        {
            health.LastSuccessfulBackup = $"{lastSuccess.JobName} ({lastSuccess.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm})";
        }
        else
        {
            health.LastSuccessfulBackup = "No successful runs recorded";
        }

        // 5. Pending and Failed counts
        var allJobs = await _configService.LoadJobsAsync();
        var enabledJobs = allJobs.Where(j => j.Enabled).ToList();
        health.PendingJobsCount = enabledJobs.Count;

        var failedRecent = allRuns.Take(10).Count(r => r.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase));
        health.FailedJobsCount = failedRecent;

        health.HealthHeadline = failedRecent > 0
            ? $"{failedRecent} recent failure(s) require attention"
            : "All backup systems running normally";

        SystemHealth = health;
    }

    private static ActivityRowVm MapToRowVm(RunHistoryRecord r)
    {
        var k = (r.Kind ?? "").ToLowerInvariant();
        var src = (r.SourceFolder ?? "").ToLowerInvariant();
        string typeDisplay = "Folder";

        if (k == "sqlserver" || k == "sql" || src.StartsWith("sql:"))
        {
            if (r.SqlDatabases.Count > 0)
            {
                typeDisplay = $"SQL ({r.SqlDatabases[0].BackupType})";
            }
            else if (src.Contains(":diff"))
            {
                typeDisplay = "SQL (Diff)";
            }
            else if (src.Contains(":log"))
            {
                typeDisplay = "SQL (Log)";
            }
            else
            {
                typeDisplay = "SQL (Full)";
            }
        }
        else if (k == "iis" || src.StartsWith("iis:"))
        {
            typeDisplay = "IIS Site";
        }

        return new ActivityRowVm
        {
            RunId = r.RunId,
            JobId = r.JobId,
            JobName = r.JobName,
            Timestamp = r.Timestamp,
            Kind = r.Kind ?? "folder",
            BackupTypeDisplay = typeDisplay,
            Status = r.Status ?? "UNKNOWN",
            Trigger = r.Trigger ?? "Manual",
            ArchiveSizeBytes = r.ArchiveSizeBytes,
            DurationSeconds = r.DurationSeconds,
            ArchiveName = r.ArchiveName ?? "",
            SourceFolder = r.SourceFolder ?? "",
            ErrorMessage = r.ErrorMessage,
            LogFile = r.LogFile,
            Destinations = r.Destinations,
            SqlDatabases = r.SqlDatabases,
            RawRecord = r
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes < 1024L * 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        return $"{bytes / (1024.0 * 1024 * 1024 * 1024):F2} TB";
    }

    // -------------------------------------------------------------------------
    // 8. EXPORT OPERATIONS (§3)
    // -------------------------------------------------------------------------
    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = $"UniBackup_Activity_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };

        if (sfd.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("RunId,JobName,Timestamp,Type,Status,SizeBytes,DurationSeconds,Source,ArchiveName,Destinations,ErrorMessage");

            foreach (var h in FilteredActivityRows)
            {
                var dests = string.Join(";", h.Destinations.Select(d => $"{d.Destination}:{d.Status}"));
                sb.AppendLine($"\"{h.RunId}\",\"{h.JobName}\",\"{h.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{h.BackupTypeDisplay}\",\"{h.Status}\",{h.ArchiveSizeBytes},{h.DurationSeconds:F1},\"{h.SourceFolder}\",\"{h.ArchiveName}\",\"{dests}\",\"{h.ErrorMessage?.Replace("\"", "\"\"")}\"");
            }

            await File.WriteAllTextAsync(sfd.FileName, sb.ToString(), Encoding.UTF8);
            NotificationHub.ShowSuccess($"Exported {FilteredActivityRows.Count} activity records to CSV successfully!", "Export Finished");
        }
    }

    [RelayCommand]
    public async Task ExportSummaryReportAsync()
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text Report (*.txt)|*.txt|CSV Summary (*.csv)|*.csv",
            FileName = $"UniBackup_Operational_Report_{DateTime.Now:yyyyMMdd}.txt"
        };

        if (sfd.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                 UNIBACKUP PRO — OPERATIONAL ANALYTICS REPORT                  ");
            sb.AppendLine($" Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | Reporting Window: {DateRangeDisplay}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine("KEY PERFORMANCE INDICATORS:");
            sb.AppendLine($"  • Total Executions:      {TotalBackupsCount} ({TotalBackupsDeltaDisplay})");
            sb.AppendLine($"  • Successful:            {SuccessfulCount} ({SuccessRateDisplay})");
            sb.AppendLine($"  • Failed / Partial:      {FailedCount + PartialCount} ({FailureRateDisplay})");
            sb.AppendLine($"  • Total Data Backed Up:  {TotalDataDisplay} ({AvgRunSizeDisplay})");
            sb.AppendLine();
            sb.AppendLine("BACKUP TYPE DISTRIBUTION:");
            foreach (var slice in BackupTypeSlices)
            {
                sb.AppendLine($"  • {slice.Name,-20}: {slice.Count,4} runs ({slice.PercentageDisplay})");
            }
            sb.AppendLine();
            sb.AppendLine("SYSTEM HEALTH SNAPSHOT:");
            sb.AppendLine($"  • Backup Engine:         {SystemHealth.EngineStatus}");
            sb.AppendLine($"  • Task Scheduler:        {SystemHealth.SchedulerStatus} ({SystemHealth.SchedulerNextRun})");
            sb.AppendLine($"  • Cloud Storage:         {SystemHealth.StorageConnectivity}");
            sb.AppendLine($"  • Last Successful Run:   {SystemHealth.LastSuccessfulBackup}");
            sb.AppendLine($"  • Health Headline:       {SystemHealth.HealthHeadline}");
            sb.AppendLine();
            sb.AppendLine("RECENT EXECUTIONS (First 50):");
            sb.AppendLine(string.Format("{0,-20} {1,-24} {2,-14} {3,-10} {4,10} {5,8}", "Date & Time", "Job Name", "Type", "Status", "Size", "Duration"));
            sb.AppendLine(new string('-', 90));

            foreach (var r in FilteredActivityRows.Take(50))
            {
                sb.AppendLine(string.Format("{0,-20} {1,-24} {2,-14} {3,-10} {4,10} {5,8}",
                    r.TimestampDisplay,
                    Truncate(r.JobName, 24),
                    r.BackupTypeDisplay,
                    r.Status,
                    r.SizeDisplay,
                    r.DurationDisplay));
            }

            sb.AppendLine();
            sb.AppendLine("End of Report.");

            await File.WriteAllTextAsync(sfd.FileName, sb.ToString(), Encoding.UTF8);
            NotificationHub.ShowSuccess($"Saved Operational Analytics Report to {Path.GetFileName(sfd.FileName)}", "Report Generated");
        }
    }

    private static string Truncate(string val, int maxLen)
    {
        if (string.IsNullOrEmpty(val)) return "";
        return val.Length <= maxLen ? val : val.Substring(0, maxLen - 2) + "..";
    }
}
