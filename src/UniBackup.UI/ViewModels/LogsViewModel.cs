using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using UniBackup.Core.Common;
using UniBackup.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniBackup.UI.ViewModels;

public sealed record LogTally(int Errors, int Warnings, DateTime Stamp, long Length);

public sealed record LogFileRow(
    string Name,
    string DisplayTitle,
    string Source,
    string Kind,
    string Path,
    string Size,
    int Errors,
    int Warnings,
    DateTime Written)
{
    public bool IsManual => Kind == "Manual";
    public bool IsService => Kind == "Service";
    public bool HasErrors => Errors > 0;
    public bool HasWarnings => Warnings > 0;
    public bool HasProblems => Errors > 0 || Warnings > 0;

    public string Clock => Written.ToString("h:mm tt");

    public string GroupLabel => Kind switch
    {
        "Manual" => "PRIMARY STREAMS",
        "Service" => "PRIMARY STREAMS",
        _ => "LEGACY ARCHIVE"
    };

    public static LogTally Tally(FileInfo file)
    {
        var errors = 0;
        var warnings = 0;

        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("[ERR]", StringComparison.Ordinal)
                    || line.Contains("[FTL]", StringComparison.Ordinal)
                    || line.Contains("| ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    errors++;
                }
                else if (line.Contains("[WRN]", StringComparison.Ordinal)
                    || line.Contains("| WARNING", StringComparison.OrdinalIgnoreCase))
                {
                    warnings++;
                }
            }
        }
        catch
        {
            // Ignore concurrent write issues
        }

        return new LogTally(errors, warnings, file.LastWriteTime, file.Length);
    }
}

public sealed partial class LogLine : ObservableObject
{
    public string Raw { get; set; } = "";
    public string RawText => Raw;
    public int Number { get; set; }
    public string Level { get; set; } = "INF";
    public string Time { get; set; } = "";
    public string Message { get; set; } = "";

    public bool IsError { get; set; }
    public bool IsWarning { get; set; }
    public bool IsSuccess { get; set; }
    public bool IsInformation => !IsError && !IsWarning;

    public bool HasTime => !string.IsNullOrWhiteSpace(Time);
    public bool HasLevel => !string.IsNullOrWhiteSpace(Level);

    [ObservableProperty]
    private bool _highlighted;

    [ObservableProperty]
    private bool _isSelected;

    public DateTime Timestamp { get; set; } = DateTime.MinValue;
    public string TimestampDisplay => Timestamp != DateTime.MinValue 
        ? Timestamp.ToString("yyyy-MM-dd HH:mm:ss") 
        : (string.IsNullOrWhiteSpace(Time) ? "-" : Time);

    public string Severity { get; set; } = "Information";
    public string Source { get; set; } = "System";
    public string JobName { get; set; } = "-";
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public bool HasStackTrace => !string.IsNullOrWhiteSpace(StackTrace);
    public bool HasException => !string.IsNullOrWhiteSpace(ExceptionType);
    public string? FilePath { get; set; }

    public string FullDetails
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {TimestampDisplay}");
            sb.AppendLine($"Severity:  {Severity} [{Level}]");
            sb.AppendLine($"Source:    {Source}");
            if (JobName != "-") sb.AppendLine($"Job Name:  {JobName}");
            if (!string.IsNullOrWhiteSpace(ExceptionType)) sb.AppendLine($"Exception: {ExceptionType}");
            sb.AppendLine($"Message:   {Message}");
            if (!string.IsNullOrWhiteSpace(StackTrace))
            {
                sb.AppendLine();
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(StackTrace);
            }
            return sb.ToString();
        }
    }

    private static readonly Regex SerilogPattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}|\d{2}-\d{2}-\d{4})[ T](?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s*(?:[+-]\d{2}:\d{2})?\s*\[(?<lvl>[A-Z]{3})\]\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex LegacyPattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+\|\s+(?<lvl>[A-Z]+)\s+\|\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ExceptionRegex = new(
        @"([A-Za-z0-9_.]+(?:Exception|Error))(?::|\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex JobQuoteRegex = new(
        @"['""](?<job>[A-Za-z0-9_\-\.]{2,40})['""]",
        RegexOptions.Compiled);

    public LogLine(string raw, int number)
    {
        Raw = raw;
        Number = number;

        var m1 = SerilogPattern.Match(raw);
        if (m1.Success)
        {
            var dateStr = m1.Groups["date"].Value;
            var timeStr = m1.Groups["time"].Value;
            if (timeStr.Contains('.')) timeStr = timeStr.Substring(0, timeStr.IndexOf('.'));
            Time = timeStr;
            Level = m1.Groups["lvl"].Value;
            Message = m1.Groups["msg"].Value;

            if (DateTime.TryParse($"{dateStr} {timeStr}", out var dt))
            {
                Timestamp = dt;
            }
        }
        else
        {
            var m2 = LegacyPattern.Match(raw);
            if (m2.Success)
            {
                var dateStr = m2.Groups["date"].Value;
                var timeStr = m2.Groups["time"].Value;
                Time = timeStr;
                var lvlRaw = m2.Groups["lvl"].Value.Trim();
                Level = lvlRaw switch
                {
                    "INFO" => "INF",
                    "WARNING" => "WRN",
                    "ERROR" => "ERR",
                    "DEBUG" => "DBG",
                    _ => lvlRaw.Length > 3 ? lvlRaw[..3] : lvlRaw
                };
                Message = m2.Groups["msg"].Value;

                if (DateTime.TryParse($"{dateStr} {timeStr}", out var dt))
                {
                    Timestamp = dt;
                }
            }
            else
            {
                Time = "";
                Level = "";
                Message = raw;
            }
        }

        IsError = Level is "ERR" or "FTL" || 
                  Message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || 
                  Message.Contains("Exception", StringComparison.OrdinalIgnoreCase) || 
                  Message.Contains("crashed", StringComparison.OrdinalIgnoreCase) ||
                  Message.TrimStart().StartsWith("at ") ||
                  Message.TrimStart().StartsWith("--->");

        IsWarning = Level == "WRN" || Message.Contains("WARNING", StringComparison.OrdinalIgnoreCase);
        IsSuccess = Message.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) || Message.Contains("✔", StringComparison.Ordinal);

        Severity = IsError ? "Error" : IsWarning ? "Warning" : "Information";

        // Extract Exception Type
        var exMatch = ExceptionRegex.Match(Message);
        if (exMatch.Success)
        {
            ExceptionType = exMatch.Groups[1].Value;
        }

        // Extract Job Name
        var jobMatch = JobQuoteRegex.Match(Message);
        if (jobMatch.Success)
        {
            JobName = jobMatch.Groups["job"].Value;
        }
        else
        {
            JobName = "-";
        }

        // Detect Source
        Source = InferSource(Message);
    }

    public static string InferSource(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "System";

        if (text.Contains("iis", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("website", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("app pool", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("binding", StringComparison.OrdinalIgnoreCase))
        {
            return "IIS Sites";
        }

        if (text.Contains("scheduler", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cron", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("task scheduler", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("scheduled window", StringComparison.OrdinalIgnoreCase))
        {
            return "Scheduler";
        }

        if (text.Contains("restore", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("drill", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("recovery", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloud Recovery";
        }

        if (text.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("email", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("webhook", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("slack", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("teams", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("discord", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("telegram", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pushover", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ntfy", StringComparison.OrdinalIgnoreCase))
        {
            return "Notification Service";
        }

        if (text.Contains("backup", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pipeline", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("archive", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("staging", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sql", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("upload", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("storage", StringComparison.OrdinalIgnoreCase))
        {
            return "Backup Engine";
        }

        return "System";
    }

    public static LogLine FromMultiLine(string firstLine, List<string> continuationLines, int number, string? filePath = null)
    {
        var item = new LogLine(firstLine, number)
        {
            FilePath = filePath
        };

        if (continuationLines.Count > 0)
        {
            var rawBuilder = new StringBuilder(firstLine);
            var stackBuilder = new StringBuilder();

            foreach (var line in continuationLines)
            {
                rawBuilder.AppendLine();
                rawBuilder.Append(line);

                var trimmed = line.Trim();
                if (trimmed.StartsWith("at ") || trimmed.StartsWith("--->") || trimmed.Contains("Exception"))
                {
                    if (string.IsNullOrEmpty(item.ExceptionType))
                    {
                        var exMatch = ExceptionRegex.Match(trimmed);
                        if (exMatch.Success) item.ExceptionType = exMatch.Groups[1].Value;
                    }
                    stackBuilder.AppendLine(line);
                }
                else if (stackBuilder.Length > 0)
                {
                    stackBuilder.AppendLine(line);
                }
            }

            item.Raw = rawBuilder.ToString();
            if (stackBuilder.Length > 0)
            {
                item.StackTrace = stackBuilder.ToString().TrimEnd();
            }
        }

        return item;
    }
}

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 5000;
    private static readonly ConcurrentDictionary<string, LogTally> Tallies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Self-reference for seamless DataContext binding when LogsView DataContext is either MainViewModel or LogsViewModel.
    /// </summary>
    public LogsViewModel Logs => this;

    private List<LogLine> _parsedLines = [];
    private List<LogLine> _filteredRecords = [];

    [ObservableProperty]
    private LogFileRow? _selectedFile;

    [ObservableProperty]
    private string _fileFilter = "";

    [ObservableProperty]
    private bool _problemsOnly;

    [ObservableProperty]
    private bool _showArchivedLogs;

    [ObservableProperty]
    private string _searchInLog = "";

    [ObservableProperty]
    private string _activeLevelFilter = "ALL";

    [ObservableProperty]
    private int _lineCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _infoCount;

    [ObservableProperty]
    private int _problemPosition;

    [ObservableProperty]
    private int _problemTotal;

    [ObservableProperty]
    private string _problemLabel = "0 problems";

    // Live counts for category chips
    [ObservableProperty]
    private int _countAll;

    [ObservableProperty]
    private int _countErrors;

    [ObservableProperty]
    private int _countWarnings;

    [ObservableProperty]
    private int _countInformation;

    // Live counts for source filters
    [ObservableProperty]
    private int _countBackupEngine;

    [ObservableProperty]
    private int _countScheduler;

    [ObservableProperty]
    private int _countIisSites;

    [ObservableProperty]
    private int _countCloudRecovery;

    [ObservableProperty]
    private int _countNotificationService;

    [ObservableProperty]
    private int _countSystem;

    // Severity Filters (Multi-select)
    [ObservableProperty]
    private bool _filterErrors = true;

    [ObservableProperty]
    private bool _filterWarnings = true;

    [ObservableProperty]
    private bool _filterInformation = true;

    public bool ShowAll
    {
        get => FilterErrors && FilterWarnings && FilterInformation;
        set
        {
            if (value)
            {
                FilterErrors = true;
                FilterWarnings = true;
                FilterInformation = true;
                ActiveLevelFilter = "ALL";
            }
        }
    }

    public bool ShowErrors
    {
        get => FilterErrors && !FilterWarnings && !FilterInformation;
        set
        {
            if (value)
            {
                FilterErrors = true;
                FilterWarnings = false;
                FilterInformation = false;
                ActiveLevelFilter = "ERR";
            }
            else
            {
                FilterErrors = false;
            }
        }
    }

    public bool ShowWarnings
    {
        get => !FilterErrors && FilterWarnings && !FilterInformation;
        set
        {
            if (value)
            {
                FilterErrors = false;
                FilterWarnings = true;
                FilterInformation = false;
                ActiveLevelFilter = "WRN";
            }
            else
            {
                FilterWarnings = false;
            }
        }
    }

    public bool ShowInfo
    {
        get => !FilterErrors && !FilterWarnings && FilterInformation;
        set
        {
            if (value)
            {
                FilterErrors = false;
                FilterWarnings = false;
                FilterInformation = true;
                ActiveLevelFilter = "INF";
            }
            else
            {
                FilterInformation = false;
            }
        }
    }

    // Source Filters (Multi-select)
    [ObservableProperty]
    private bool _filterBackupEngine = true;

    [ObservableProperty]
    private bool _filterScheduler = true;

    [ObservableProperty]
    private bool _filterIisSites = true;

    [ObservableProperty]
    private bool _filterCloudRecovery = true;

    [ObservableProperty]
    private bool _filterNotificationService = true;

    [ObservableProperty]
    private bool _filterSystem = true;

    // Date Range Filters
    [ObservableProperty]
    private string _selectedDateRange = "All Time";

    [ObservableProperty]
    private DateTime _customStartDate = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    private DateTime _customEndDate = DateTime.Today;

    public bool IsCustomDateRange => SelectedDateRange == "Custom range";

    public List<string> DateRangeOptions { get; } =
    [
        "All Time",
        "Today",
        "Last 7 days",
        "Last 30 days",
        "Last 90 days",
        "Custom range"
    ];

    // Left Panel & Details Panel Layout States
    [ObservableProperty]
    private bool _isFilterPanelCollapsed;

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private bool _isCompactView;

    [ObservableProperty]
    private LogLine? _selectedLog;

    // Selection & Bulk Action States
    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectedCountText = "0 logs selected";

    [ObservableProperty]
    private bool _isAnySelected;

    [ObservableProperty]
    private bool? _isAllFilteredSelected = false;

    // Navigation State
    [ObservableProperty]
    private bool _canGoFirstError;

    [ObservableProperty]
    private bool _canGoPreviousError;

    [ObservableProperty]
    private bool _canGoNextError;

    [ObservableProperty]
    private bool _canGoLatestError;

    [ObservableProperty]
    private bool _canGoPreviousLog;

    [ObservableProperty]
    private bool _canGoNextLog;

    // Pagination State
    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _pageSize = 25;

    [ObservableProperty]
    private int _totalFilteredCount;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private string _paginationText = "Page 1 of 1";

    public List<int> PageSizes { get; } = [10, 25, 50, 100];

    public bool CanGoFirstPage => CurrentPage > 1;
    public bool CanGoPreviousPage => CurrentPage > 1;
    public bool CanGoNextPage => CurrentPage < TotalPages;
    public bool CanGoLastPage => CurrentPage < TotalPages;

    public event EventHandler<LogLine>? ScrollRequested;

    [ObservableProperty]
    private string _statusMessage = "Loading logs...";

    [ObservableProperty]
    private bool _autoScroll = true;

    public ObservableCollection<LogFileRow> Files { get; } = [];
    public ObservableCollection<LogFileRow> VisibleFiles { get; } = [];
    public ObservableCollection<LogLine> VisibleLines { get; } = [];
    public ObservableCollection<LogLine> PagedLogs { get; } = [];

    [ObservableProperty]
    private bool _hasNoVisibleLines;

    public LogsViewModel()
    {
    }

    public async Task InitializeAsync()
    {
        await RefreshLogsAsync();
    }

    partial void OnFileFilterChanged(string value) => FilterFiles();
    partial void OnProblemsOnlyChanged(bool value) => FilterFiles();
    partial void OnShowArchivedLogsChanged(bool value) => _ = RefreshLogsAsync();

    partial void OnSearchInLogChanged(string value) => ApplyFiltersAndPagination();

    partial void OnFilterErrorsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowErrors));
        ApplyFiltersAndPagination();
    }

    partial void OnFilterWarningsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowWarnings));
        ApplyFiltersAndPagination();
    }

    partial void OnFilterInformationChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowInfo));
        ApplyFiltersAndPagination();
    }

    partial void OnFilterBackupEngineChanged(bool value) => ApplyFiltersAndPagination();
    partial void OnFilterSchedulerChanged(bool value) => ApplyFiltersAndPagination();
    partial void OnFilterIisSitesChanged(bool value) => ApplyFiltersAndPagination();
    partial void OnFilterCloudRecoveryChanged(bool value) => ApplyFiltersAndPagination();
    partial void OnFilterNotificationServiceChanged(bool value) => ApplyFiltersAndPagination();
    partial void OnFilterSystemChanged(bool value) => ApplyFiltersAndPagination();

    partial void OnSelectedDateRangeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomDateRange));
        ApplyFiltersAndPagination();
    }

    partial void OnCustomStartDateChanged(DateTime value)
    {
        if (IsCustomDateRange) ApplyFiltersAndPagination();
    }

    partial void OnCustomEndDateChanged(DateTime value)
    {
        if (IsCustomDateRange) ApplyFiltersAndPagination();
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoFirstPage));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(CanGoLastPage));
        UpdatePageSlice();
    }

    partial void OnPageSizeChanged(int value)
    {
        CurrentPage = 1;
        ApplyFiltersAndPagination();
    }

    partial void OnActiveLevelFilterChanged(string value)
    {
        if (value == "ALL")
        {
            _filterErrors = true;
            _filterWarnings = true;
            _filterInformation = true;
        }
        else if (value == "ERR")
        {
            _filterErrors = true;
            _filterWarnings = false;
            _filterInformation = false;
        }
        else if (value == "WRN")
        {
            _filterErrors = false;
            _filterWarnings = true;
            _filterInformation = false;
        }
        else if (value == "INF")
        {
            _filterErrors = false;
            _filterWarnings = false;
            _filterInformation = true;
        }

        OnPropertyChanged(nameof(FilterErrors));
        OnPropertyChanged(nameof(FilterWarnings));
        OnPropertyChanged(nameof(FilterInformation));
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowErrors));
        OnPropertyChanged(nameof(ShowWarnings));
        OnPropertyChanged(nameof(ShowInfo));
        ApplyFiltersAndPagination();
    }

    partial void OnSelectedFileChanged(LogFileRow? value)
    {
        if (value != null)
        {
            _ = LoadFileContentAsync(value);
        }
    }

    partial void OnSelectedLogChanged(LogLine? value)
    {
        UpdateNavigationState();
    }

    [RelayCommand]
    public async Task RefreshLogsAsync()
    {
        StatusMessage = "Scanning log files...";
        var previousPath = SelectedFile?.Path;

        var scanned = await Task.Run(() =>
        {
            var list = new List<LogFileRow>();
            var dir = AppPaths.DefaultLogsDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Always ensure the 2 primary operational logs exist and are seeded
            UniBackup.Infrastructure.Logging.LoggingSetup.EnsureLogFilesExist();

            // 1. Primary Manual Run Log
            var manualPath = UniBackup.Infrastructure.Logging.LoggingSetup.ManualLogPath;
            var manualFi = new FileInfo(manualPath);
            if (manualFi.Exists)
            {
                if (!Tallies.TryGetValue(manualFi.FullName, out var tally) || tally.Stamp != manualFi.LastWriteTime || tally.Length != manualFi.Length)
                {
                    tally = LogFileRow.Tally(manualFi);
                    Tallies[manualFi.FullName] = tally;
                }

                list.Add(new LogFileRow(
                    manualFi.Name,
                    "Manual Run",
                    "Interactive GUI",
                    "Manual",
                    manualFi.FullName,
                    FormattingUtils.FormatBytes(manualFi.Length),
                    tally.Errors,
                    tally.Warnings,
                    manualFi.LastWriteTime));
            }

            // 2. Primary Service Run Log
            var servicePath = UniBackup.Infrastructure.Logging.LoggingSetup.ServiceLogPath;
            var serviceFi = new FileInfo(servicePath);
            if (serviceFi.Exists)
            {
                if (!Tallies.TryGetValue(serviceFi.FullName, out var tally) || tally.Stamp != serviceFi.LastWriteTime || tally.Length != serviceFi.Length)
                {
                    tally = LogFileRow.Tally(serviceFi);
                    Tallies[serviceFi.FullName] = tally;
                }

                list.Add(new LogFileRow(
                    serviceFi.Name,
                    "Service Run",
                    "Service & Scheduler",
                    "Service",
                    serviceFi.FullName,
                    FormattingUtils.FormatBytes(serviceFi.Length),
                    tally.Errors,
                    tally.Warnings,
                    serviceFi.LastWriteTime));
            }

            // 3. Optional Legacy/Archived logs if toggled on
            if (ShowArchivedLogs)
            {
                var dirInfo = new DirectoryInfo(dir);
                foreach (var fi in dirInfo.GetFiles("*.log").OrderByDescending(f => f.LastWriteTime))
                {
                    if (fi.FullName.Equals(manualPath, StringComparison.OrdinalIgnoreCase) ||
                        fi.FullName.Equals(servicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!Tallies.TryGetValue(fi.FullName, out var tally) || tally.Stamp != fi.LastWriteTime || tally.Length != fi.Length)
                    {
                        tally = LogFileRow.Tally(fi);
                        Tallies[fi.FullName] = tally;
                    }

                    list.Add(new LogFileRow(
                        fi.Name,
                        fi.Name,
                        "Archived Log",
                        "Archived",
                        fi.FullName,
                        FormattingUtils.FormatBytes(fi.Length),
                        tally.Errors,
                        tally.Warnings,
                        fi.LastWriteTime));
                }
            }

            return list;
        });

        Files.Clear();
        foreach (var f in scanned) Files.Add(f);

        FilterFiles();

        SelectedFile = Files.FirstOrDefault(f => f.Path == previousPath) ?? Files.FirstOrDefault();
        if (SelectedFile == null)
        {
            _parsedLines.Clear();
            _filteredRecords.Clear();
            PagedLogs.Clear();
            VisibleLines.Clear();
            HasNoVisibleLines = true;
            StatusMessage = "No log files found in " + AppPaths.DefaultLogsDirectory;
            UpdateCounts();
        }
    }

    private void FilterFiles()
    {
        VisibleFiles.Clear();
        foreach (var file in Files)
        {
            if (ProblemsOnly && !file.HasProblems) continue;
            if (!string.IsNullOrWhiteSpace(FileFilter) && 
                !file.Name.Contains(FileFilter, StringComparison.OrdinalIgnoreCase) &&
                !file.DisplayTitle.Contains(FileFilter, StringComparison.OrdinalIgnoreCase)) continue;

            VisibleFiles.Add(file);
        }
    }

    public async Task LoadFileContentAsync(LogFileRow file)
    {
        StatusMessage = $"Reading {file.Name}...";
        try
        {
            var parsed = await Task.Run(() =>
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);

                var tail = lines.Count > MaxLines ? lines.Skip(lines.Count - MaxLines).ToList() : lines;
                var startNumber = lines.Count - tail.Count + 1;

                var events = new List<LogLine>();
                string? currentHeader = null;
                var continuation = new List<string>();
                var currentNumber = startNumber;

                var serilogHeaderRegex = new Regex(@"^(?:\d{4}-\d{2}-\d{2}|\d{2}-\d{2}-\d{4})[ T]\d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

                for (int i = 0; i < tail.Count; i++)
                {
                    var line = tail[i];
                    if (serilogHeaderRegex.IsMatch(line) || line.Contains("| ERROR |") || line.Contains("| INFO |") || line.Contains("| WARNING |"))
                    {
                        if (currentHeader != null)
                        {
                            events.Add(LogLine.FromMultiLine(currentHeader, continuation, currentNumber, file.Path));
                            continuation.Clear();
                        }
                        currentHeader = line;
                        currentNumber = startNumber + i;
                    }
                    else
                    {
                        if (currentHeader != null)
                        {
                            continuation.Add(line);
                        }
                        else
                        {
                            currentHeader = line;
                            currentNumber = startNumber + i;
                        }
                    }
                }

                if (currentHeader != null)
                {
                    events.Add(LogLine.FromMultiLine(currentHeader, continuation, currentNumber, file.Path));
                }

                return events;
            });

            _parsedLines = parsed;
            foreach (var r in _parsedLines)
            {
                r.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(LogLine.IsSelected))
                    {
                        UpdateSelectionState();
                    }
                };
            }

            CurrentPage = 1;
            ApplyFiltersAndPagination();

            StatusMessage = $"{_parsedLines.Count} log events loaded ({ErrorCount} errors, {WarningCount} warnings)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading log: {ex.Message}";
            _parsedLines.Clear();
            _filteredRecords.Clear();
            PagedLogs.Clear();
            VisibleLines.Clear();
            HasNoVisibleLines = true;
            UpdateCounts();
        }
    }

    /// <summary>
    /// Programmatically loads custom or test log lines without needing a disk file.
    /// </summary>
    public void LoadCustomLines(IEnumerable<LogLine> lines)
    {
        _parsedLines = lines.ToList();
        foreach (var r in _parsedLines)
        {
            r.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LogLine.IsSelected))
                {
                    UpdateSelectionState();
                }
            };
        }
        CurrentPage = 1;
        ApplyFiltersAndPagination();
    }

    public void ApplyFiltersAndPagination()
    {
        var query = _parsedLines.AsEnumerable();

        // 1. Severity filters (multi-selection)
        bool allSeveritiesSelected = FilterErrors && FilterWarnings && FilterInformation;
        bool noSeveritiesSelected = !FilterErrors && !FilterWarnings && !FilterInformation;

        if (!allSeveritiesSelected && !noSeveritiesSelected)
        {
            query = query.Where(r => 
                (FilterErrors && r.IsError) ||
                (FilterWarnings && r.IsWarning) ||
                (FilterInformation && r.IsInformation));
        }
        else if (noSeveritiesSelected)
        {
            query = Enumerable.Empty<LogLine>();
        }

        // 2. Source filters (multi-selection)
        bool allSourcesSelected = FilterBackupEngine && FilterScheduler && FilterIisSites && 
                                  FilterCloudRecovery && FilterNotificationService && FilterSystem;
        bool noSourcesSelected = !FilterBackupEngine && !FilterScheduler && !FilterIisSites && 
                                 !FilterCloudRecovery && !FilterNotificationService && !FilterSystem;

        if (!allSourcesSelected && !noSourcesSelected)
        {
            query = query.Where(r =>
                (FilterBackupEngine && r.Source == "Backup Engine") ||
                (FilterScheduler && r.Source == "Scheduler") ||
                (FilterIisSites && r.Source == "IIS Sites") ||
                (FilterCloudRecovery && r.Source == "Cloud Recovery") ||
                (FilterNotificationService && r.Source == "Notification Service") ||
                (FilterSystem && r.Source == "System"));
        }
        else if (noSourcesSelected)
        {
            query = Enumerable.Empty<LogLine>();
        }

        // 3. Date range filter
        if (SelectedDateRange == "Today")
        {
            var today = DateTime.Today;
            query = query.Where(r => r.Timestamp >= today);
        }
        else if (SelectedDateRange == "Last 7 days")
        {
            var cutoff = DateTime.Now.AddDays(-7);
            query = query.Where(r => r.Timestamp >= cutoff);
        }
        else if (SelectedDateRange == "Last 30 days")
        {
            var cutoff = DateTime.Now.AddDays(-30);
            query = query.Where(r => r.Timestamp >= cutoff);
        }
        else if (SelectedDateRange == "Last 90 days")
        {
            var cutoff = DateTime.Now.AddDays(-90);
            query = query.Where(r => r.Timestamp >= cutoff);
        }
        else if (SelectedDateRange == "Custom range")
        {
            var start = CustomStartDate.Date;
            var end = CustomEndDate.Date.AddDays(1).AddTicks(-1);
            query = query.Where(r => r.Timestamp >= start && r.Timestamp <= end);
        }

        // 4. Search text (Message, Exception, Stack Trace, Source, Job Name)
        var st = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(st))
        {
            query = query.Where(r =>
                (r.Message != null && r.Message.Contains(st, StringComparison.OrdinalIgnoreCase)) ||
                (r.ExceptionType != null && r.ExceptionType.Contains(st, StringComparison.OrdinalIgnoreCase)) ||
                (r.StackTrace != null && r.StackTrace.Contains(st, StringComparison.OrdinalIgnoreCase)) ||
                (r.Source != null && r.Source.Contains(st, StringComparison.OrdinalIgnoreCase)) ||
                (r.JobName != null && r.JobName.Contains(st, StringComparison.OrdinalIgnoreCase)) ||
                (r.Raw != null && r.Raw.Contains(st, StringComparison.OrdinalIgnoreCase)));
        }

        _filteredRecords = query.ToList();
        TotalFilteredCount = _filteredRecords.Count;

        TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalFilteredCount / PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
        if (CurrentPage < 1) CurrentPage = 1;

        UpdateCounts();
        UpdatePageSlice();
        UpdateSelectionState();
        UpdateNavigationState();
    }

    private void UpdatePageSlice()
    {
        PaginationText = $"Page {CurrentPage} of {TotalPages}";

        var pageSlice = _filteredRecords
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        PagedLogs.Clear();
        foreach (var item in pageSlice)
        {
            PagedLogs.Add(item);
        }

        VisibleLines.Clear();
        foreach (var item in PagedLogs)
        {
            VisibleLines.Add(item);
        }

        HasNoVisibleLines = PagedLogs.Count == 0;

        OnPropertyChanged(nameof(CanGoFirstPage));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(CanGoLastPage));
    }

    private void UpdateCounts()
    {
        CountAll = _parsedLines.Count;
        CountErrors = _parsedLines.Count(r => r.IsError);
        CountWarnings = _parsedLines.Count(r => r.IsWarning);
        CountInformation = _parsedLines.Count(r => r.IsInformation);

        CountBackupEngine = _parsedLines.Count(r => r.Source == "Backup Engine");
        CountScheduler = _parsedLines.Count(r => r.Source == "Scheduler");
        CountIisSites = _parsedLines.Count(r => r.Source == "IIS Sites");
        CountCloudRecovery = _parsedLines.Count(r => r.Source == "Cloud Recovery");
        CountNotificationService = _parsedLines.Count(r => r.Source == "Notification Service");
        CountSystem = _parsedLines.Count(r => r.Source == "System");

        ErrorCount = CountErrors;
        WarningCount = CountWarnings;
        InfoCount = CountInformation;
        LineCount = CountAll;

        ProblemTotal = _filteredRecords.Count(r => r.IsError || r.IsWarning);
        if (ProblemTotal == 0)
        {
            ProblemPosition = 0;
        }
        else if (ProblemPosition > ProblemTotal || ProblemPosition == 0)
        {
            ProblemPosition = 1;
        }
        UpdateProblemLabel();
    }

    public void UpdateSelectionState()
    {
        var selectedInFiltered = _filteredRecords.Count(r => r.IsSelected);
        var totalSelected = _parsedLines.Count(r => r.IsSelected);

        SelectedCount = totalSelected;
        IsAnySelected = SelectedCount > 0;
        SelectedCountText = $"{SelectedCount} log{(SelectedCount == 1 ? "" : "s")} selected";

        if (_filteredRecords.Count == 0 || selectedInFiltered == 0)
        {
            IsAllFilteredSelected = false;
        }
        else if (selectedInFiltered == _filteredRecords.Count)
        {
            IsAllFilteredSelected = true;
        }
        else
        {
            IsAllFilteredSelected = null;
        }
    }

    private void UpdateNavigationState()
    {
        var errors = _filteredRecords.Where(r => r.IsError).ToList();
        if (errors.Count == 0)
        {
            CanGoFirstError = false;
            CanGoLatestError = false;
            CanGoNextError = false;
            CanGoPreviousError = false;
        }
        else if (SelectedLog == null)
        {
            CanGoFirstError = true;
            CanGoLatestError = true;
            CanGoNextError = true;
            CanGoPreviousError = false;
        }
        else
        {
            var currentIdx = _filteredRecords.IndexOf(SelectedLog);
            var firstErrorIdx = _filteredRecords.IndexOf(errors.First());
            var lastErrorIdx = _filteredRecords.IndexOf(errors.Last());

            CanGoFirstError = currentIdx != firstErrorIdx;
            CanGoLatestError = currentIdx != lastErrorIdx;
            CanGoPreviousError = currentIdx > firstErrorIdx;
            CanGoNextError = currentIdx < lastErrorIdx;
        }

        if (SelectedLog == null)
        {
            CanGoPreviousLog = false;
            CanGoNextLog = _filteredRecords.Count > 0;
        }
        else
        {
            var currentIdx = _filteredRecords.IndexOf(SelectedLog);
            CanGoPreviousLog = currentIdx > 0;
            CanGoNextLog = currentIdx >= 0 && currentIdx < _filteredRecords.Count - 1;
        }
    }

    // =========================================================================
    // SELECTION COMMANDS
    // =========================================================================

    [RelayCommand]
    public void SelectAllFiltered()
    {
        foreach (var record in _filteredRecords)
        {
            record.IsSelected = true;
        }
        UpdateSelectionState();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var record in _parsedLines)
        {
            record.IsSelected = false;
        }
        UpdateSelectionState();
    }

    [RelayCommand]
    public void ToggleSelectAllFiltered()
    {
        if (IsAllFilteredSelected == true)
        {
            ClearSelection();
        }
        else
        {
            SelectAllFiltered();
        }
    }

    // =========================================================================
    // NAVIGATION COMMANDS
    // =========================================================================

    [RelayCommand]
    public void FirstError()
    {
        var first = _filteredRecords.FirstOrDefault(r => r.IsError);
        if (first != null) NavigateToRecord(first);
    }

    [RelayCommand]
    public void LatestError()
    {
        var last = _filteredRecords.LastOrDefault(r => r.IsError);
        if (last != null) NavigateToRecord(last);
    }

    [RelayCommand]
    public void NextError()
    {
        if (SelectedLog == null)
        {
            FirstError();
            return;
        }
        var currentIdx = _filteredRecords.IndexOf(SelectedLog);
        var next = _filteredRecords.Skip(currentIdx + 1).FirstOrDefault(r => r.IsError);
        if (next != null) NavigateToRecord(next);
    }

    [RelayCommand]
    public void PreviousError()
    {
        if (SelectedLog == null)
        {
            LatestError();
            return;
        }
        var currentIdx = _filteredRecords.IndexOf(SelectedLog);
        if (currentIdx > 0)
        {
            var prev = _filteredRecords.Take(currentIdx).LastOrDefault(r => r.IsError);
            if (prev != null) NavigateToRecord(prev);
        }
    }

    [RelayCommand]
    public void NextLog()
    {
        if (SelectedLog == null)
        {
            if (_filteredRecords.Count > 0) NavigateToRecord(_filteredRecords[0]);
            return;
        }
        var idx = _filteredRecords.IndexOf(SelectedLog);
        if (idx >= 0 && idx < _filteredRecords.Count - 1)
        {
            NavigateToRecord(_filteredRecords[idx + 1]);
        }
    }

    [RelayCommand]
    public void PreviousLog()
    {
        if (SelectedLog == null)
        {
            if (_filteredRecords.Count > 0) NavigateToRecord(_filteredRecords[^1]);
            return;
        }
        var idx = _filteredRecords.IndexOf(SelectedLog);
        if (idx > 0)
        {
            NavigateToRecord(_filteredRecords[idx - 1]);
        }
    }

    public void NavigateToRecord(LogLine target)
    {
        var targetIndex = _filteredRecords.IndexOf(target);
        if (targetIndex < 0) return;

        var targetPage = (targetIndex / PageSize) + 1;
        if (CurrentPage != targetPage)
        {
            CurrentPage = targetPage;
        }

        foreach (var r in _parsedLines) r.Highlighted = false;
        target.Highlighted = true;
        SelectedLog = target;
        IsDetailsOpen = true;

        UpdateNavigationState();
        ScrollRequested?.Invoke(this, target);
    }

    // Backwards compatibility for tests
    [RelayCommand]
    public void NextProblem()
    {
        var problems = _filteredRecords.Where(l => l.IsError || l.IsWarning).ToList();
        if (problems.Count == 0)
        {
            problems = _parsedLines.Where(l => l.IsError || l.IsWarning).ToList();
            if (problems.Count == 0) return;
        }

        var next = ProblemPosition + 1;
        if (next > problems.Count) next = 1;
        ProblemPosition = next;
        ProblemTotal = problems.Count;
        UpdateProblemLabel();

        var target = problems[next - 1];
        NavigateToRecord(target);
    }

    [RelayCommand]
    public void PreviousProblem()
    {
        var problems = _filteredRecords.Where(l => l.IsError || l.IsWarning).ToList();
        if (problems.Count == 0)
        {
            problems = _parsedLines.Where(l => l.IsError || l.IsWarning).ToList();
            if (problems.Count == 0) return;
        }

        var prev = ProblemPosition - 1;
        if (prev < 1) prev = problems.Count;
        ProblemPosition = prev;
        ProblemTotal = problems.Count;
        UpdateProblemLabel();

        var target = problems[prev - 1];
        NavigateToRecord(target);
    }

    private void UpdateProblemLabel()
    {
        if (ProblemTotal == 0)
        {
            ProblemLabel = "0 problems";
        }
        else
        {
            ProblemLabel = $"{ProblemPosition} of {ProblemTotal} problems";
        }
    }

    // =========================================================================
    // PAGINATION COMMANDS
    // =========================================================================

    [RelayCommand]
    public void FirstPage() => CurrentPage = 1;

    [RelayCommand]
    public void PreviousPage() => CurrentPage = Math.Max(1, CurrentPage - 1);

    [RelayCommand]
    public void NextPage() => CurrentPage = Math.Min(TotalPages, CurrentPage + 1);

    [RelayCommand]
    public void LastPage() => CurrentPage = TotalPages;

    // =========================================================================
    // FILTER MANAGEMENT COMMANDS
    // =========================================================================

    [RelayCommand]
    public void ClearFilters()
    {
        FilterErrors = true;
        FilterWarnings = true;
        FilterInformation = true;
        FilterBackupEngine = true;
        FilterScheduler = true;
        FilterIisSites = true;
        FilterCloudRecovery = true;
        FilterNotificationService = true;
        FilterSystem = true;
        SelectedDateRange = "All Time";
        SearchInLog = "";
    }

    [RelayCommand]
    public void SetLevelFilter(string? filter)
    {
        ActiveLevelFilter = filter ?? "ALL";
    }

    public string SearchText
    {
        get => SearchInLog;
        set => SearchInLog = value;
    }

    public string SearchFilter
    {
        get => SearchInLog;
        set => SearchInLog = value;
    }

    [RelayCommand]
    public void ToggleFilterPanel()
    {
        IsFilterPanelCollapsed = !IsFilterPanelCollapsed;
    }

    [RelayCommand]
    public void ToggleCompactView()
    {
        IsCompactView = !IsCompactView;
    }

    // =========================================================================
    // DETAILS PANEL COMMANDS
    // =========================================================================

    [RelayCommand]
    public void ViewDetails(LogLine? line)
    {
        if (line != null)
        {
            NavigateToRecord(line);
        }
        else
        {
            IsDetailsOpen = true;
        }
    }

    [RelayCommand]
    public void CloseDetails()
    {
        IsDetailsOpen = false;
    }

    [RelayCommand]
    public void CopyDetailsMessage()
    {
        if (SelectedLog == null) return;
        TrySetClipboard(SelectedLog.Message);
        NotificationHub.ShowSuccess("Copied log message to clipboard.", "Copied");
    }

    [RelayCommand]
    public void CopyDetailsFull()
    {
        if (SelectedLog == null) return;
        TrySetClipboard(SelectedLog.FullDetails);
        NotificationHub.ShowSuccess("Copied full details to clipboard.", "Copied");
    }

    [RelayCommand]
    public void CopySelectedDetails()
    {
        var selected = _parsedLines.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0 && SelectedLog != null)
        {
            selected = [SelectedLog];
        }
        if (selected.Count == 0) return;

        var text = string.Join(Environment.NewLine + "----------------------------------------" + Environment.NewLine,
            selected.Select(r => r.FullDetails));

        if (TrySetClipboard(text))
        {
            NotificationHub.ShowSuccess($"Copied details of {selected.Count} log{(selected.Count == 1 ? "" : "s")} to clipboard.", "Copied to Clipboard");
        }
    }

    [RelayCommand]
    public void CopyAll()
    {
        if (_filteredRecords.Count == 0) return;
        var text = string.Join(Environment.NewLine, _filteredRecords.Select(l => l.Raw));
        if (TrySetClipboard(text))
        {
            NotificationHub.ShowSuccess($"Copied {_filteredRecords.Count} lines to clipboard.", "Copied to Clipboard");
        }
    }

    [RelayCommand]
    public void CopyFiltered() => CopyAll();

    [RelayCommand]
    public void OpenLogFileLocation()
    {
        var path = SelectedLog?.FilePath ?? SelectedFile?.Path ?? AppPaths.DefaultLogsDirectory;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                var dir = Directory.Exists(path) ? path : AppPaths.DefaultLogsDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Could not open location: {ex.Message}", "Error");
        }
    }

    [RelayCommand]
    public void OpenInNotepad()
    {
        if (SelectedFile != null && File.Exists(SelectedFile.Path))
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SelectedFile.Path}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    public void OpenFolder() => OpenLogFileLocation();

    [RelayCommand]
    public void OpenLogsFolder() => OpenLogFileLocation();

    // =========================================================================
    // EXPORT COMMANDS
    // =========================================================================

    [RelayCommand]
    public async Task ExportAllLogsAsync()
    {
        await ExportLogsAsync(_parsedLines, "UniBackup_AllLogs.csv");
    }

    [RelayCommand]
    public async Task ExportFilteredLogsAsync()
    {
        await ExportLogsAsync(_filteredRecords, "UniBackup_FilteredLogs.csv");
    }

    [RelayCommand]
    public async Task ExportSelectedLogsAsync()
    {
        var selected = _parsedLines.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            NotificationHub.ShowWarning("No logs are currently selected to export.", "Export Logs");
            return;
        }
        await ExportLogsAsync(selected, "UniBackup_SelectedLogs.csv");
    }

    public async Task<string> ExportLogsAsync(IEnumerable<LogLine> logs, string defaultFileName, string? explicitPath = null)
    {
        var list = logs.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Severity,Level,Source,JobName,Message,ExceptionType,StackTrace");
        foreach (var r in list)
        {
            sb.AppendLine($"\"{EscapeCsv(r.TimestampDisplay)}\",\"{EscapeCsv(r.Severity)}\",\"{EscapeCsv(r.Level)}\",\"{EscapeCsv(r.Source)}\",\"{EscapeCsv(r.JobName)}\",\"{EscapeCsv(r.Message)}\",\"{EscapeCsv(r.ExceptionType ?? "")}\",\"{EscapeCsv(r.StackTrace ?? "")}\"");
        }

        var content = sb.ToString();

        string? targetPath = explicitPath;
        if (string.IsNullOrEmpty(targetPath))
        {
            var app = System.Windows.Application.Current;
            if (app != null && app.Dispatcher != null && app.Dispatcher.Thread.IsAlive && !app.Dispatcher.HasShutdownStarted)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = defaultFileName,
                        DefaultExt = ".csv",
                        Filter = "CSV files (*.csv)|*.csv|Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                        Title = "Export Application Logs"
                    };

                    if (dlg.ShowDialog() == true)
                    {
                        targetPath = dlg.FileName;
                    }
                }
                else
                {
                    try
                    {
                        targetPath = app.Dispatcher.Invoke(() =>
                        {
                            var dlg = new Microsoft.Win32.SaveFileDialog
                            {
                                FileName = defaultFileName,
                                DefaultExt = ".csv",
                                Filter = "CSV files (*.csv)|*.csv|Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                                Title = "Export Application Logs"
                            };

                            return dlg.ShowDialog() == true ? dlg.FileName : null;
                        }, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // Fallback or non-interactive
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(targetPath))
        {
            await File.WriteAllTextAsync(targetPath, content);
            NotificationHub.ShowSuccess($"Exported {list.Count} log records to {Path.GetFileName(targetPath)}.", "Export Succeeded");
        }

        return content;
    }

    private static string EscapeCsv(string? s) => s?.Replace("\"", "\"\"") ?? "";

    private static bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
