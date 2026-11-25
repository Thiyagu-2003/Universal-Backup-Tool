using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.UI.Services;

namespace UniBackup.UI.ViewModels;

public partial class MainViewModel
{
    // =========================================================================
    // 1. BACKUP JOBS HEADER & DATE-RANGE STATE
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJobsPreset7Days))]
    [NotifyPropertyChangedFor(nameof(IsJobsPreset30Days))]
    [NotifyPropertyChangedFor(nameof(IsJobsPreset90Days))]
    private string _jobsDatePreset = "Last30Days";

    [ObservableProperty]
    private string _jobsDateRangeDisplay = "Last 30 days";

    [ObservableProperty]
    private string _jobsLastRefreshedDisplay = "Just now";

    [ObservableProperty]
    private bool _isJobsRefreshing;

    public bool IsJobsPreset7Days => JobsDatePreset == "Last7Days";
    public bool IsJobsPreset30Days => JobsDatePreset == "Last30Days";
    public bool IsJobsPreset90Days => JobsDatePreset == "Last90Days";

    [RelayCommand]
    public void SelectJobsPreset(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return;
        JobsDatePreset = preset;
        UpdateJobsDateRangeLabel();
        UpdateJobsActivityChart();
    }

    private void UpdateJobsDateRangeLabel()
    {
        var today = DateTime.Today;
        switch (JobsDatePreset)
        {
            case "Last7Days":
                JobsDateRangeDisplay = $"{today.AddDays(-7):MMM dd} – {today:MMM dd, yyyy} (7 days)";
                break;
            case "Last90Days":
                JobsDateRangeDisplay = $"{today.AddDays(-90):MMM dd} – {today:MMM dd, yyyy} (90 days)";
                break;
            case "Last30Days":
            default:
                JobsDateRangeDisplay = $"{today.AddDays(-30):MMM dd} – {today:MMM dd, yyyy} (30 days)";
                break;
        }
    }

    [RelayCommand]
    public async Task RefreshJobsPageAsync()
    {
        if (IsJobsRefreshing) return;
        IsJobsRefreshing = true;
        try
        {
            await RefreshJobsAsync();
            await RefreshHistoryAsync();
            JobsLastRefreshedDisplay = $"Today, {DateTime.Now:h:mm tt}";
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to refresh backup jobs dashboard");
        }
        finally
        {
            IsJobsRefreshing = false;
        }
    }

    // =========================================================================
    // 2. KPI SUMMARY CARDS SUPPORTING METRICS
    // =========================================================================

    [ObservableProperty]
    private string _jobsTotalSubText = "0 active · 0 disabled";

    [ObservableProperty]
    private string _jobsEnabledSubText = "Ready for execution";

    [ObservableProperty]
    private string _jobsNextScheduledSubText = "No upcoming runs";

    [ObservableProperty]
    private string _jobsAttentionSubText = "0 failed · 0 partial · 0 stale";

    // =========================================================================
    // 3. BACKUP ACTIVITY TRENDS CHART (LiveCharts2)
    // =========================================================================

    [ObservableProperty]
    private ISeries[] _jobsActivitySeries = [];

    [ObservableProperty]
    private Axis[] _jobsActivityXAxes = [];

    [ObservableProperty]
    private Axis[] _jobsActivityYAxes = [];

    [ObservableProperty]
    private bool _hasJobsActivityHistory;

    // =========================================================================
    // 4. BACKUP JOB DISTRIBUTION DONUT CHART (LiveCharts2)
    // =========================================================================

    [ObservableProperty]
    private ISeries[] _jobsDistributionSeries = [];

    public ObservableCollection<BackupTypeSliceVm> JobsDistributionSlices { get; } = [];

    [ObservableProperty]
    private int _jobsDistributionTotalCount;

    [ObservableProperty]
    private bool _hasJobsDistribution;

    // =========================================================================
    // 5. FILTERING, SEARCH, SORTING & PAGINATION STATE
    // =========================================================================

    private List<JobCardViewModel> _allJobsCache = [];

    public ObservableCollection<JobCardViewModel> JobsPagedRows { get; } = [];

    [ObservableProperty]
    private string _jobsSearchQuery = "";

    partial void OnJobsSearchQueryChanged(string value)
    {
        JobsCurrentPage = 1;
        ApplyJobsTableFilterSortAndPagination();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterEnabled))]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterDisabled))]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterRunning))]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterFailed))]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterNeedsAttention))]
    [NotifyPropertyChangedFor(nameof(IsJobsFilterScheduled))]
    private string _jobsStatusFilter = "All";

    public bool IsJobsFilterAll => string.Equals(JobsStatusFilter, "All", StringComparison.OrdinalIgnoreCase);
    public bool IsJobsFilterEnabled => string.Equals(JobsStatusFilter, "Enabled", StringComparison.OrdinalIgnoreCase);
    public bool IsJobsFilterDisabled => string.Equals(JobsStatusFilter, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsJobsFilterRunning => string.Equals(JobsStatusFilter, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsJobsFilterFailed => string.Equals(JobsStatusFilter, "Failed", StringComparison.OrdinalIgnoreCase);
    public bool IsJobsFilterNeedsAttention => string.Equals(JobsStatusFilter, "NeedsAttention", StringComparison.OrdinalIgnoreCase);
    public bool IsJobsFilterScheduled => string.Equals(JobsStatusFilter, "Scheduled", StringComparison.OrdinalIgnoreCase);

    partial void OnJobsStatusFilterChanged(string value)
    {
        JobsCurrentPage = 1;
        ApplyJobsTableFilterSortAndPagination();
    }

    [RelayCommand]
    public void SetJobsStatusFilter(string filter)
    {
        JobsStatusFilter = string.IsNullOrWhiteSpace(filter) ? "All" : filter;
    }

    [RelayCommand]
    public void ResetJobsFilters()
    {
        JobsSearchQuery = "";
        JobsStatusFilter = "All";
        JobsCurrentPage = 1;
        ApplyJobsTableFilterSortAndPagination();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortedByName))]
    [NotifyPropertyChangedFor(nameof(IsSortedByLastRun))]
    [NotifyPropertyChangedFor(nameof(IsSortedByNextRun))]
    [NotifyPropertyChangedFor(nameof(IsSortedByStatus))]
    [NotifyPropertyChangedFor(nameof(IsSortedByType))]
    private string _jobsSortColumn = "Name";

    [ObservableProperty]
    private bool _jobsSortAscending = true;

    public bool IsSortedByName => JobsSortColumn == "Name";
    public bool IsSortedByLastRun => JobsSortColumn == "LastRun";
    public bool IsSortedByNextRun => JobsSortColumn == "NextRun";
    public bool IsSortedByStatus => JobsSortColumn == "Status";
    public bool IsSortedByType => JobsSortColumn == "Type";

    [RelayCommand]
    public void SortJobsBy(string column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;
        if (string.Equals(JobsSortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            JobsSortAscending = !JobsSortAscending;
        }
        else
        {
            JobsSortColumn = column;
            JobsSortAscending = column == "Name" || column == "Type" || column == "NextRun";
        }
        JobsCurrentPage = 1;
        ApplyJobsTableFilterSortAndPagination();
    }

    public ObservableCollection<int> AvailableJobsPageSizes { get; } = [5, 10, 20, 50];

    [ObservableProperty]
    private int _jobsPageSize = 10;

    partial void OnJobsPageSizeChanged(int value)
    {
        if (value <= 0) return;
        JobsCurrentPage = 1;
        UpdateJobsPagedRows();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJobsPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanJobsNextPage))]
    private int _jobsCurrentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJobsPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanJobsNextPage))]
    private int _jobsTotalPages = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFilteredJobs))]
    private int _jobsFilteredCount;

    [ObservableProperty]
    private string _jobsPaginationSummary = "Showing 0 of 0 jobs";

    public bool CanJobsPrevPage => JobsCurrentPage > 1;
    public bool CanJobsNextPage => JobsCurrentPage < JobsTotalPages;
    public bool HasNoFilteredJobs => !HasNoJobs && JobsFilteredCount == 0;

    private List<JobCardViewModel> _jobsFilteredList = [];

    [RelayCommand]
    public void JobsFirstPage()
    {
        JobsCurrentPage = 1;
        UpdateJobsPagedRows();
    }

    [RelayCommand]
    public void JobsPrevPage()
    {
        if (CanJobsPrevPage)
        {
            JobsCurrentPage--;
            UpdateJobsPagedRows();
        }
    }

    [RelayCommand]
    public void JobsNextPage()
    {
        if (CanJobsNextPage)
        {
            JobsCurrentPage++;
            UpdateJobsPagedRows();
        }
    }

    [RelayCommand]
    public void JobsLastPage()
    {
        JobsCurrentPage = JobsTotalPages;
        UpdateJobsPagedRows();
    }

    // =========================================================================
    // 6. MULTI-SELECTION & BULK ACTIONS
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedJobs))]
    private int _selectedJobsCount;

    public bool HasSelectedJobs => SelectedJobsCount > 0;

    [ObservableProperty]
    private bool _isAllCurrentPageSelected;

    private bool _isUpdatingSelectAll;

    partial void OnIsAllCurrentPageSelectedChanged(bool value)
    {
        if (_isUpdatingSelectAll) return;
        _isUpdatingSelectAll = true;
        try
        {
            foreach (var job in JobsPagedRows)
            {
                job.IsSelected = value;
            }
            UpdateSelectedJobsCount();
        }
        finally
        {
            _isUpdatingSelectAll = false;
        }
    }

    public void UpdateSelectedJobsCount()
    {
        var source = GetSourceJobCards();
        SelectedJobsCount = source.Count(j => j.IsSelected);

        if (!_isUpdatingSelectAll)
        {
            _isUpdatingSelectAll = true;
            try
            {
                IsAllCurrentPageSelected = JobsPagedRows.Count > 0 && JobsPagedRows.All(j => j.IsSelected);
            }
            finally
            {
                _isUpdatingSelectAll = false;
            }
        }
    }

    [RelayCommand]
    public void ClearJobSelection()
    {
        _isUpdatingSelectAll = true;
        try
        {
            foreach (var job in GetSourceJobCards())
            {
                job.IsSelected = false;
            }
            IsAllCurrentPageSelected = false;
            SelectedJobsCount = 0;
        }
        finally
        {
            _isUpdatingSelectAll = false;
        }
    }

    [RelayCommand]
    public async Task BulkEnableJobsAsync()
    {
        await BulkSetEnabledAsync(true);
    }

    [RelayCommand]
    public async Task BulkDisableJobsAsync()
    {
        await BulkSetEnabledAsync(false);
    }

    private async Task BulkSetEnabledAsync(bool enable)
    {
        var selectedIds = GetSourceJobCards().Where(j => j.IsSelected).Select(j => j.Id).ToHashSet();
        if (selectedIds.Count == 0) return;

        var allJobs = await _configService.LoadJobsAsync();
        int changed = 0;
        foreach (var job in allJobs.Where(j => selectedIds.Contains(j.Id)))
        {
            if (job.Enabled != enable)
            {
                job.Enabled = enable;
                await _schedulerService.SyncJobTaskAsync(job);
                changed++;
            }
        }

        if (changed > 0)
        {
            await _configService.SaveJobsAsync(allJobs);
        }

        ClearJobSelection();
        await RefreshJobsAsync();
        NotificationHub.ShowSuccess(
            $"{(enable ? "Enabled" : "Disabled")} {selectedIds.Count} backup {(selectedIds.Count == 1 ? "job" : "jobs")}.",
            enable ? "Jobs Enabled" : "Jobs Disabled");
    }

    [RelayCommand]
    public async Task BulkRunSelectedJobsAsync()
    {
        var selected = GetSourceJobCards().Where(j => j.IsSelected).ToList();
        if (selected.Count == 0) return;

        NotificationHub.ShowInfo(
            $"Starting execution for {selected.Count} selected {(selected.Count == 1 ? "job" : "jobs")}...",
            "Bulk Backup Started");

        foreach (var card in selected)
        {
            await RunJobAsync(card);
        }

        ClearJobSelection();
    }

    // =========================================================================
    // 7. JOB DETAILS FLYOUT / MODAL
    // =========================================================================

    [ObservableProperty]
    private JobCardViewModel? _selectedJobDetails;

    [ObservableProperty]
    private bool _isJobDetailsOpen;

    public ObservableCollection<RunHistoryRecord> SelectedJobRecentHistory { get; } = [];

    [ObservableProperty]
    private bool _hasSelectedJobHistory;

    [RelayCommand]
    public void OpenJobDetails(JobCardViewModel? card)
    {
        if (card == null) return;
        SelectedJobDetails = card;
        SelectedJobRecentHistory.Clear();

        var matches = HistoryRecords
            .Where(h => string.Equals(h.JobId, card.Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(h.JobName, card.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Timestamp)
            .Take(8)
            .ToList();

        foreach (var h in matches)
        {
            SelectedJobRecentHistory.Add(h);
        }

        HasSelectedJobHistory = SelectedJobRecentHistory.Count > 0;
        IsJobDetailsOpen = true;
    }

    [RelayCommand]
    public void CloseJobDetails()
    {
        IsJobDetailsOpen = false;
        SelectedJobDetails = null;
    }

    [RelayCommand]
    public async Task RunSelectedJobDetailsAsync()
    {
        var card = SelectedJobDetails;
        if (card == null) return;
        CloseJobDetails();
        await RunJobAsync(card);
    }

    [RelayCommand]
    public void EditSelectedJobDetails()
    {
        var card = SelectedJobDetails;
        if (card == null) return;
        CloseJobDetails();
        EditJob(card);
    }

    [RelayCommand]
    public void ViewSelectedJobLogs()
    {
        CloseJobDetails();
        ActiveTab = "Logs";
    }

    // =========================================================================
    // 8. INITIALIZATION & DATA AGGREGATION
    // =========================================================================

    public void InitJobsPage()
    {
        UpdateJobsDateRangeLabel();
        BuildDefaultJobsCharts();
        Jobs.CollectionChanged += OnJobsCollectionChanged;
        UpdateJobsPageData();
    }

    private void OnJobsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (JobCardViewModel card in e.NewItems)
            {
                card.PropertyChanged -= OnJobCardPropertyChanged;
                card.PropertyChanged += OnJobCardPropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (JobCardViewModel card in e.OldItems)
            {
                card.PropertyChanged -= OnJobCardPropertyChanged;
            }
        }

        // Keep UI synced even if Jobs is mutated directly (e.g. in tests)
        if (_allJobsCache.Count == 0 || Jobs.Count > _allJobsCache.Count)
        {
            UpdateJobsPageData();
        }
        else
        {
            ApplyJobsTableFilterSortAndPagination();
        }
    }

    private void OnJobCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobCardViewModel.IsSelected))
        {
            UpdateSelectedJobsCount();
        }
    }

    private void BuildDefaultJobsCharts()
    {
        JobsActivityXAxes = new Axis[]
        {
            new()
            {
                LabelsPaint = new SolidColorPaint(new SKColor(0x64, 0x74, 0x8B)),
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xE2, 0xE8, 0xF0)) { StrokeThickness = 1 }
            }
        };

        JobsActivityYAxes = new Axis[]
        {
            new()
            {
                LabelsPaint = new SolidColorPaint(new SKColor(0x64, 0x74, 0x8B)),
                TextSize = 11,
                MinLimit = 0,
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xE2, 0xE8, 0xF0)) { StrokeThickness = 1 }
            }
        };

        JobsActivitySeries = Array.Empty<ISeries>();
        JobsDistributionSeries = Array.Empty<ISeries>();
    }

    private List<JobCardViewModel> GetSourceJobCards()
    {
        if (_allJobsCache.Count > 0) return _allJobsCache;
        return Jobs.ToList();
    }

    public void SetAllJobsCache(IEnumerable<BackupJob> loadedJobs)
    {
        foreach (var old in _allJobsCache)
        {
            old.PropertyChanged -= OnJobCardPropertyChanged;
        }
        _allJobsCache = loadedJobs.Select(j =>
        {
            var card = new JobCardViewModel(j);
            card.PropertyChanged += OnJobCardPropertyChanged;
            return card;
        }).ToList();
    }

    public void UpdateJobsPageData()
    {
        var sourceJobs = GetSourceJobCards();
        int total = TotalJobsCount > 0 ? TotalJobsCount : sourceJobs.Count;
        int enabled = EnabledJobsCount > 0 || sourceJobs.Count == 0 ? EnabledJobsCount : sourceJobs.Count(j => j.Enabled);
        int disabled = Math.Max(0, total - enabled);

        JobsTotalSubText = $"{enabled} active · {disabled} disabled";
        JobsEnabledSubText = enabled == 0
            ? "No active jobs ready"
            : $"{enabled} {(enabled == 1 ? "job" : "jobs")} ready for execution";

        var nextJob = sourceJobs
            .Where(j => j.Enabled && j.Model.Schedule != null && !string.Equals(j.Model.Schedule.Freq, "manual", StringComparison.OrdinalIgnoreCase))
            .OrderBy(j => j.NextRunSortKey)
            .FirstOrDefault();

        JobsNextScheduledSubText = nextJob != null
            ? $"Next: {nextJob.NextRunDisplay}"
            : "No scheduled triggers";

        int failed = sourceJobs.Count(j => string.Equals(j.Model.LastRunStatus, "FAILED", StringComparison.OrdinalIgnoreCase));
        int partial = sourceJobs.Count(j => string.Equals(j.Model.LastRunStatus, "PARTIAL", StringComparison.OrdinalIgnoreCase));
        JobsAttentionSubText = $"{failed} failed · {partial} partial · {StaleJobsCount} stale";

        OnPropertyChanged(nameof(HasNoFilteredJobs));

        UpdateJobsActivityChart();
        BuildJobsDistributionDonut(sourceJobs);
        ApplyJobsTableFilterSortAndPagination();
    }

    private void UpdateJobsActivityChart()
    {
        int windowDays = JobsDatePreset switch
        {
            "Last7Days" => 7,
            "Last90Days" => 90,
            _ => 30
        };

        var startUtc = DateTime.UtcNow.AddDays(-windowDays);
        var activeRuns = HistoryRecords
            .Where(r => r.Timestamp >= startUtc)
            .ToList();

        var dateLabels = new List<string>();
        var successVals = new List<double>();
        var failVals = new List<double>();
        var partialVals = new List<double>();

        var runsByDate = activeRuns
            .GroupBy(r => r.Timestamp.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var today = DateTime.Today;
        int step = windowDays > 30 ? 3 : 1;
        int points = windowDays / step;

        for (int i = points - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i * step);
            dateLabels.Add(day.ToString("MM/dd"));

            if (runsByDate.TryGetValue(day, out var dayRuns))
            {
                successVals.Add(dayRuns.Count(r => string.Equals(r.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)));
                failVals.Add(dayRuns.Count(r => string.Equals(r.Status, "FAILED", StringComparison.OrdinalIgnoreCase)));
                partialVals.Add(dayRuns.Count(r => string.Equals(r.Status, "PARTIAL", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                successVals.Add(0);
                failVals.Add(0);
                partialVals.Add(0);
            }
        }

        HasJobsActivityHistory = activeRuns.Count > 0;

        JobsActivitySeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Name = "Successful",
                Values = successVals,
                Stroke = new SolidColorPaint(new SKColor(0x00, 0x94, 0x6A), 2.5f),
                Fill = new SolidColorPaint(new SKColor(0x00, 0x94, 0x6A, 0x24)),
                GeometrySize = 5,
                GeometryStroke = new SolidColorPaint(new SKColor(0x00, 0x94, 0x6A), 2f),
                GeometryFill = new SolidColorPaint(SKColors.White)
            },
            new LineSeries<double>
            {
                Name = "Failed",
                Values = failVals,
                Stroke = new SolidColorPaint(new SKColor(0xE0, 0x29, 0x3F), 2.5f),
                Fill = new SolidColorPaint(new SKColor(0xE0, 0x29, 0x3F, 0x20)),
                GeometrySize = 5,
                GeometryStroke = new SolidColorPaint(new SKColor(0xE0, 0x29, 0x3F), 2f),
                GeometryFill = new SolidColorPaint(SKColors.White)
            },
            new LineSeries<double>
            {
                Name = "Partial",
                Values = partialVals,
                Stroke = new SolidColorPaint(new SKColor(0xC9, 0x7A, 0x00), 2.5f),
                Fill = new SolidColorPaint(new SKColor(0xC9, 0x7A, 0x00, 0x20)),
                GeometrySize = 5,
                GeometryStroke = new SolidColorPaint(new SKColor(0xC9, 0x7A, 0x00), 2f),
                GeometryFill = new SolidColorPaint(SKColors.White)
            }
        };

        JobsActivityXAxes = new Axis[]
        {
            new()
            {
                Labels = dateLabels,
                LabelsPaint = new SolidColorPaint(new SKColor(0x64, 0x74, 0x8B)),
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xE2, 0xE8, 0xF0)) { StrokeThickness = 1 }
            }
        };

        JobsActivityYAxes = new Axis[]
        {
            new()
            {
                LabelsPaint = new SolidColorPaint(new SKColor(0x64, 0x74, 0x8B)),
                TextSize = 11,
                MinLimit = 0,
                SeparatorsPaint = new SolidColorPaint(new SKColor(0xE2, 0xE8, 0xF0)) { StrokeThickness = 1 }
            }
        };
    }

    private void BuildJobsDistributionDonut(List<JobCardViewModel> sourceJobs)
    {
        int fullCount = 0;
        int diffCount = 0;
        int logCount = 0;
        int folderCount = 0;
        int iisCount = 0;

        foreach (var card in sourceJobs)
        {
            if (card.IsSqlServer)
            {
                fullCount++;
                if (card.Model.Schedule?.Diff?.Enabled == true) diffCount++;
                if (card.Model.Schedule?.Log?.Enabled == true) logCount++;
            }
            else if (card.IsIis)
            {
                iisCount++;
            }
            else
            {
                folderCount++;
            }
        }

        JobsDistributionTotalCount = sourceJobs.Count;
        HasJobsDistribution = JobsDistributionTotalCount > 0;
        int sliceTotal = Math.Max(1, fullCount + diffCount + logCount + folderCount + iisCount);

        JobsDistributionSlices.Clear();
        var allSlices = new List<BackupTypeSliceVm>
        {
            new() { Name = "Full Backup", Count = fullCount, Percentage = ((double)fullCount / sliceTotal) * 100.0, ColorHex = "#2D5BFF" },
            new() { Name = "Differential", Count = diffCount, Percentage = ((double)diffCount / sliceTotal) * 100.0, ColorHex = "#0A7FD4" },
            new() { Name = "Transaction Log", Count = logCount, Percentage = ((double)logCount / sliceTotal) * 100.0, ColorHex = "#00946A" },
            new() { Name = "Folder Backup", Count = folderCount, Percentage = ((double)folderCount / sliceTotal) * 100.0, ColorHex = "#7C4DFF" },
            new() { Name = "IIS Website", Count = iisCount, Percentage = ((double)iisCount / sliceTotal) * 100.0, ColorHex = "#00A0AE" }
        };

        var pieSeries = new List<ISeries>();
        foreach (var slice in allSlices.Where(s => s.Count > 0))
        {
            JobsDistributionSlices.Add(slice);
            var skColor = SKColor.Parse(slice.ColorHex);
            pieSeries.Add(new PieSeries<int>
            {
                Name = slice.Name,
                Values = new[] { slice.Count },
                Fill = new SolidColorPaint(skColor),
                InnerRadius = 52
            });
        }

        JobsDistributionSeries = pieSeries.ToArray();
    }

    public void ApplyJobsTableFilterSortAndPagination()
    {
        IEnumerable<JobCardViewModel> query = GetSourceJobCards();

        // 1. Search by name, source, or backup type
        if (!string.IsNullOrWhiteSpace(JobsSearchQuery))
        {
            var term = JobsSearchQuery.Trim();
            query = query.Where(j =>
                j.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                j.EffectiveSourcePath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                j.KindChipName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // 2. Status / Category Filter
        query = JobsStatusFilter switch
        {
            "Enabled" => query.Where(j => j.Enabled),
            "Disabled" => query.Where(j => !j.Enabled),
            "Running" => query.Where(j => j.IsRunning),
            "Failed" => query.Where(j => j.HasFailed),
            "NeedsAttention" => query.Where(j => JobNeedsAttention(j.Model)),
            "Scheduled" => query.Where(j => j.Enabled && !string.Equals(j.Model.Schedule?.Freq, "manual", StringComparison.OrdinalIgnoreCase)),
            "SqlServer" => query.Where(j => j.IsSqlServer),
            "FoldersAndSites" => query.Where(j => !j.IsSqlServer),
            _ => query
        };

        // 3. Multi-column Sorting
        query = JobsSortColumn switch
        {
            "LastRun" => JobsSortAscending
                ? query.OrderBy(j => j.LastRunSortKey).ThenBy(j => j.Name)
                : query.OrderByDescending(j => j.LastRunSortKey).ThenBy(j => j.Name),
            "NextRun" => JobsSortAscending
                ? query.OrderBy(j => j.NextRunSortKey).ThenBy(j => j.Name)
                : query.OrderByDescending(j => j.NextRunSortKey).ThenBy(j => j.Name),
            "Status" => JobsSortAscending
                ? query.OrderBy(j => j.StatusSortOrder).ThenBy(j => j.Name)
                : query.OrderByDescending(j => j.StatusSortOrder).ThenBy(j => j.Name),
            "Type" => JobsSortAscending
                ? query.OrderBy(j => j.KindChipName).ThenBy(j => j.Name)
                : query.OrderByDescending(j => j.KindChipName).ThenBy(j => j.Name),
            _ => JobsSortAscending
                ? query.OrderByDescending(j => j.Pinned).ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(j => j.Pinned).ThenByDescending(j => j.Name, StringComparer.OrdinalIgnoreCase)
        };

        _jobsFilteredList = query.ToList();
        JobsFilteredCount = _jobsFilteredList.Count;
        OnPropertyChanged(nameof(HasNoFilteredJobs));

        UpdateJobsPagedRows();
        UpdateSelectedJobsCount();
    }

    private void UpdateJobsPagedRows()
    {
        int size = Math.Max(1, JobsPageSize);
        JobsTotalPages = Math.Max(1, (int)Math.Ceiling(JobsFilteredCount / (double)size));
        if (JobsCurrentPage > JobsTotalPages) JobsCurrentPage = JobsTotalPages;
        if (JobsCurrentPage < 1) JobsCurrentPage = 1;

        var pageItems = _jobsFilteredList
            .Skip((JobsCurrentPage - 1) * size)
            .Take(size)
            .ToList();

        JobsPagedRows.Clear();
        foreach (var item in pageItems)
        {
            JobsPagedRows.Add(item);
        }

        if (JobsFilteredCount == 0)
        {
            JobsPaginationSummary = "Showing 0 of 0 jobs";
        }
        else
        {
            int start = ((JobsCurrentPage - 1) * size) + 1;
            int end = Math.Min(JobsCurrentPage * size, JobsFilteredCount);
            JobsPaginationSummary = $"Showing {start}–{end} of {JobsFilteredCount} jobs";
        }

        OnPropertyChanged(nameof(CanJobsPrevPage));
        OnPropertyChanged(nameof(CanJobsNextPage));
        UpdateSelectedJobsCount();
    }
}
