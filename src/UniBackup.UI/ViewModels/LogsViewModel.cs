using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    public string Raw { get; }
    public string RawText => Raw;
    public int Number { get; }
    public string Level { get; }
    public string Time { get; }
    public string Message { get; }

    public bool IsError { get; }
    public bool IsWarning { get; }
    public bool IsSuccess { get; }

    public bool HasTime => !string.IsNullOrWhiteSpace(Time);
    public bool HasLevel => !string.IsNullOrWhiteSpace(Level);

    [ObservableProperty]
    private bool _highlighted;

    private static readonly Regex SerilogPattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}|\d{2}-\d{2}-\d{4})[ T](?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s*(?:[+-]\d{2}:\d{2})?\s*\[(?<lvl>[A-Z]{3})\]\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex LegacyPattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+\|\s+(?<lvl>[A-Z]+)\s+\|\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    public LogLine(string raw, int number)
    {
        Raw = raw;
        Number = number;

        var m1 = SerilogPattern.Match(raw);
        if (m1.Success)
        {
            Time = m1.Groups["time"].Value;
            if (Time.Contains('.')) Time = Time.Substring(0, Time.IndexOf('.'));
            Level = m1.Groups["lvl"].Value;
            Message = m1.Groups["msg"].Value;
        }
        else
        {
            var m2 = LegacyPattern.Match(raw);
            if (m2.Success)
            {
                Time = m2.Groups["time"].Value;
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
    }
}

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 2500;
    private static readonly Dictionary<string, LogTally> Tallies = new(StringComparer.OrdinalIgnoreCase);

    private List<LogLine> _parsedLines = [];

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

    public bool ShowAll
    {
        get => ActiveLevelFilter == "ALL";
        set { if (value) ActiveLevelFilter = "ALL"; }
    }

    public bool ShowErrors
    {
        get => ActiveLevelFilter == "ERR";
        set { if (value) ActiveLevelFilter = "ERR"; }
    }

    public bool ShowWarnings
    {
        get => ActiveLevelFilter == "WRN";
        set { if (value) ActiveLevelFilter = "WRN"; }
    }

    public bool ShowInfo
    {
        get => ActiveLevelFilter == "INF";
        set { if (value) ActiveLevelFilter = "INF"; }
    }

    public event EventHandler<LogLine>? ScrollRequested;

    [ObservableProperty]
    private string _statusMessage = "Loading logs...";

    [ObservableProperty]
    private bool _autoScroll = true;

    public ObservableCollection<LogFileRow> Files { get; } = [];
    public ObservableCollection<LogFileRow> VisibleFiles { get; } = [];
    public ObservableCollection<LogLine> VisibleLines { get; } = [];

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
    partial void OnSearchInLogChanged(string value) => FilterLogLines();
    partial void OnActiveLevelFilterChanged(string value)
    {
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowErrors));
        OnPropertyChanged(nameof(ShowWarnings));
        OnPropertyChanged(nameof(ShowInfo));
        FilterLogLines();
    }

    partial void OnSelectedFileChanged(LogFileRow? value)
    {
        if (value != null)
        {
            _ = LoadFileContentAsync(value);
        }
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
            VisibleLines.Clear();
            StatusMessage = "No log files found in " + AppPaths.DefaultLogsDirectory;
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

    private async Task LoadFileContentAsync(LogFileRow file)
    {
        StatusMessage = $"Reading {file.Name}...";
        try
        {
            var rawLines = await Task.Run(() =>
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var list = new List<string>();
                while (reader.ReadLine() is { } line) list.Add(line);
                return list;
            });

            var tail = rawLines.Count > MaxLines ? rawLines.Skip(rawLines.Count - MaxLines).ToList() : rawLines;
            var startNumber = rawLines.Count - tail.Count + 1;

            _parsedLines = tail.Select((line, idx) => new LogLine(line, startNumber + idx)).ToList();

            LineCount = rawLines.Count;
            ErrorCount = _parsedLines.Count(l => l.IsError);
            WarningCount = _parsedLines.Count(l => l.IsWarning);
            InfoCount = _parsedLines.Count(l => l.Level == "INF");

            var problemCount = ErrorCount + WarningCount;
            ProblemTotal = problemCount;
            ProblemPosition = problemCount > 0 ? 1 : 0;
            UpdateProblemLabel();

            StatusMessage = $"{LineCount} total lines ({ErrorCount} errors, {WarningCount} warnings)";

            FilterLogLines();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading log: {ex.Message}";
            VisibleLines.Clear();
        }
    }

    private void FilterLogLines()
    {
        VisibleLines.Clear();
        var query = _parsedLines.AsEnumerable();

        if (ActiveLevelFilter == "INF") query = query.Where(l => l.Level == "INF");
        else if (ActiveLevelFilter == "WRN") query = query.Where(l => l.IsWarning);
        else if (ActiveLevelFilter == "ERR") query = query.Where(l => l.IsError);

        if (!string.IsNullOrWhiteSpace(SearchInLog))
        {
            query = query.Where(l => l.Raw.Contains(SearchInLog, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var line in query)
        {
            VisibleLines.Add(line);
        }

        var visibleProblems = VisibleLines.Count(l => l.IsError || l.IsWarning);
        ProblemTotal = visibleProblems;
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

    [RelayCommand]
    public void NextProblem() => StepProblem(1);

    [RelayCommand]
    public void PreviousProblem() => StepProblem(-1);

    private void StepProblem(int direction)
    {
        var problems = VisibleLines.Where(l => l.IsError || l.IsWarning).ToList();
        if (problems.Count == 0)
        {
            problems = _parsedLines.Where(l => l.IsError || l.IsWarning).ToList();
            if (problems.Count == 0) return;
        }

        var next = ProblemPosition + direction;
        if (next < 1) next = problems.Count;
        if (next > problems.Count) next = 1;

        ProblemPosition = next;
        ProblemTotal = problems.Count;
        UpdateProblemLabel();

        foreach (var line in _parsedLines) line.Highlighted = false;

        var target = problems[next - 1];
        target.Highlighted = true;
        ScrollRequested?.Invoke(this, target);
    }

    [RelayCommand]
    public void FindNext()
    {
        if (string.IsNullOrWhiteSpace(SearchInLog) || VisibleLines.Count == 0) return;

        var current = VisibleLines.ToList().FindIndex(l => l.Highlighted);
        foreach (var line in _parsedLines) line.Highlighted = false;

        var found = VisibleLines.Skip(current + 1).FirstOrDefault(l => l.Raw.Contains(SearchInLog, StringComparison.OrdinalIgnoreCase))
                    ?? VisibleLines.FirstOrDefault(l => l.Raw.Contains(SearchInLog, StringComparison.OrdinalIgnoreCase));

        if (found != null)
        {
            found.Highlighted = true;
            ScrollRequested?.Invoke(this, found);
        }
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

    [RelayCommand]
    public void SetLevelFilter(string? filter)
    {
        ActiveLevelFilter = filter ?? "ALL";
    }

    public string SearchFilter
    {
        get => SearchInLog;
        set => SearchInLog = value;
    }

    [RelayCommand]
    public void CopyAll()
    {
        if (VisibleLines.Count == 0) return;
        var text = string.Join(Environment.NewLine, VisibleLines.Select(l => l.Raw));
        Clipboard.SetText(text);
        NotificationHub.ShowSuccess($"Copied {VisibleLines.Count} lines to clipboard.", "Copied to Clipboard");
    }

    [RelayCommand]
    public void CopyFiltered() => CopyAll();

    [RelayCommand]
    public void OpenInNotepad()
    {
        if (SelectedFile != null && File.Exists(SelectedFile.Path))
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SelectedFile.Path}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    public void OpenFolder()
    {
        var logsDir = AppPaths.DefaultLogsDirectory;
        Directory.CreateDirectory(logsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", logsDir) { UseShellExecute = true });
    }

    [RelayCommand]
    public void OpenLogsFolder() => OpenFolder();
}
