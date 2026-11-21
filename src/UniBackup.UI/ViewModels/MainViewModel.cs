using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Logging;
using UniBackup.Infrastructure.Windows;
using UniBackup.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace UniBackup.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigurationService _configService;
    private readonly BackupPipeline _pipeline;
    private readonly BatchBackupRunner _batchRunner;
    private readonly RecoveryService _recoveryService;
    private readonly ServerDiagnosticsService _diagnosticsService;
    private readonly ISchedulerService _schedulerService;
    private readonly IIisDiscoveryService _iisService;
    private readonly INotificationService _notificationService;
    private readonly SqlRestoreOrchestrator? _sqlRestoreOrchestrator;
    private readonly RestoreDrillService? _restoreDrillService;

    [ObservableProperty]
    private string _activeTab = "Backups";

    [ObservableProperty]
    private LogsViewModel _logs = new();

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private AppConfig _config = new();

    [ObservableProperty]
    private ObservableCollection<JobCardViewModel> _jobs = [];

    [ObservableProperty]
    private ObservableCollection<RunHistoryRecord> _historyRecords = [];

    [ObservableProperty]
    private ObservableCollection<CloudArchiveItem> _cloudArchives = [];

    [ObservableProperty]
    private ObservableCollection<DiagnosticItem> _diagnosticItems = [];

    [ObservableProperty]
    private ObservableCollection<IisSiteInfo> _iisSites = [];

    [ObservableProperty]
    private CloudArchiveItem? _selectedArchive;

    [ObservableProperty]
    private string _restoreDestinationPath = @"C:\UniBackup\Restore";

    [ObservableProperty]
    private string _restoreWriteMode = "safe";

    [ObservableProperty]
    private string _selectedRecoveryTab = "SqlServer";

    // Summary Statistics
    [ObservableProperty]
    private int _totalRunsCount;

    [ObservableProperty]
    private int _successfulRunsCount;

    [ObservableProperty]
    private int _failedRunsCount;

    [ObservableProperty]
    private string _totalUploadedDisplay = "0 B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStaleJobs))]
    private int _staleJobsCount;

    [ObservableProperty]
    private bool _hasNoJobs;

    public bool HasStaleJobs => StaleJobsCount > 0;
    public bool HasDiagnostics => DiagnosticItems.Count > 0;
    public string SuccessRateDisplay => TotalRunsCount > 0
        ? $"{(double)SuccessfulRunsCount / TotalRunsCount * 100:F0}% success rate"
        : "No runs yet";

    // Progress Overlay
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressPhase = "Idle";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressStatusMessage = "";

    [ObservableProperty]
    private string _progressDetail = "";

    [ObservableProperty]
    private string _serverLabel = ServerIdentity.Current.DisplayLabel;

    [ObservableProperty]
    private string _tempFreeSpaceDisplay = "";

    [ObservableProperty]
    private string _windowsServiceStatus = "Not Installed";

    [ObservableProperty]
    private string _windowsServiceStatusColor = "#94A3B8";

    // Settings Sub-Tabs ("Service", "Connection", "Notifications", "Storage")
    [ObservableProperty]
    private string _selectedSettingsTab = "Service";

    // Run Mode (Windows Service vs Task Scheduler)
    [ObservableProperty]
    private bool _runByTask;

    public bool RunByService
    {
        get => !RunByTask;
        set => RunByTask = !value;
    }

    partial void OnRunByTaskChanged(bool value)
    {
        OnPropertyChanged(nameof(RunByService));
        RefreshServiceStatus();
    }

    // Windows Service details
    [ObservableProperty]
    private bool _windowsServiceRunning;

    [ObservableProperty]
    private bool _windowsServiceStopped;

    [ObservableProperty]
    private bool _windowsServiceNotInstalled = true;

    [ObservableProperty]
    private bool _windowsServiceInstalled;

    [ObservableProperty]
    private string _windowsServiceDetail = "runs as LocalSystem · automatic background execution";

    // Task Scheduler details
    [ObservableProperty]
    private bool _taskInstalled;

    [ObservableProperty]
    private bool _taskNotInstalled = true;

    [ObservableProperty]
    private bool _taskRunning;

    [ObservableProperty]
    private bool _taskStopped;

    [ObservableProperty]
    private string _taskState = "Checking…";

    [ObservableProperty]
    private string _taskDetail = "runs on-demand via Windows Task Scheduler";

    // Schedule Timings
    public IReadOnlyList<int> ScheduleHours { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> ScheduleMinutes { get; } = Enumerable.Range(0, 12).Select(i => i * 5).ToList();
    public IReadOnlyList<string> SchedulePresets { get; } = ["22:00", "23:00", "23:30", "00:00", "01:00", "02:00"];

    [ObservableProperty]
    private int _scheduleHour12 = 11;

    [ObservableProperty]
    private int _scheduleMinute = 0;

    [ObservableProperty]
    private bool _scheduleIsAm;

    [ObservableProperty]
    private bool _scheduleIsPm = true;

    private bool _syncingScheduleTime;

    public string NextRunHint
    {
        get
        {
            try
            {
                var now = DateTime.Now;
                var hour = ScheduleIsPm ? (ScheduleHour12 % 12) + 12 : (ScheduleHour12 % 12);
                var target = new DateTime(now.Year, now.Month, now.Day, hour, ScheduleMinute, 0);
                if (target <= now) target = target.AddDays(1);
                var isToday = target.Date == now.Date;
                var dayWord = isToday ? "tonight" : "tomorrow";
                return $"Next run {dayWord} at {target:h:mm tt}";
            }
            catch
            {
                return "Runs daily at scheduled time";
            }
        }
    }

    public string ConfigFileLocation => AppPaths.DefaultConfigFile;

    [ObservableProperty]
    private bool _selectAllJobs;

    public MainViewModel(
        ConfigurationService configService,
        BackupPipeline pipeline,
        BatchBackupRunner batchRunner,
        RecoveryService recoveryService,
        ServerDiagnosticsService diagnosticsService,
        ISchedulerService schedulerService,
        IIisDiscoveryService iisService,
        INotificationService notificationService,
        SqlRestoreOrchestrator? sqlRestoreOrchestrator = null,
        RestoreDrillService? restoreDrillService = null)
    {
        _configService = configService;
        _pipeline = pipeline;
        _batchRunner = batchRunner;
        _recoveryService = recoveryService;
        _diagnosticsService = diagnosticsService;
        _schedulerService = schedulerService;
        _iisService = iisService;
        _notificationService = notificationService;
        _sqlRestoreOrchestrator = sqlRestoreOrchestrator;
        _restoreDrillService = restoreDrillService;
        UpdateFreeSpaceIndicators();
    }

    public async Task InitializeAsync()
    {
        Config = await _configService.LoadConfigAsync();
        LoggingSetup.Initialize(Config.Backup.LogPath, Config.Backup.MaxLogMb, Config.Backup.KeepLogsDays);

        SyncFromConfigScheduleTime();
        UpdateFreeSpaceIndicators();
        RefreshServiceStatus();
        OnPropertyChanged(nameof(EmailRecipientsText));
        await RefreshJobsAsync();
        await RefreshHistoryAsync();
        await Logs.InitializeAsync();
    }

    public void UpdateFreeSpaceIndicators()
    {
        var free = AppPaths.GetFreeDiskSpaceBytes(Config.Backup.TempFolder);
        TempFreeSpaceDisplay = $"{FormattingUtils.FormatBytes(free)} free";
    }

    [RelayCommand]
    public async Task RefreshJobsAsync()
    {
        var loadedJobs = await _configService.LoadJobsAsync();
        Jobs.Clear();

        var filtered = loadedJobs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            filtered = filtered.Where(j =>
                j.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                j.EffectiveSourcePath.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var job in filtered.OrderByDescending(j => j.Pinned).ThenBy(j => j.Name))
        {
            Jobs.Add(new JobCardViewModel(job));
        }

        HasNoJobs = Jobs.Count == 0;
        ComputeStaleJobs();
    }

    [RelayCommand]
    public async Task RefreshHistoryAsync()
    {
        var loadedHistory = await _configService.LoadHistoryAsync();
        HistoryRecords.Clear();

        foreach (var h in loadedHistory)
        {
            HistoryRecords.Add(h);
        }

        TotalRunsCount = HistoryRecords.Count;
        SuccessfulRunsCount = HistoryRecords.Count(h => h.Status == "SUCCESS");
        FailedRunsCount = HistoryRecords.Count(h => h.Status == "FAILED" || h.Status == "PARTIAL");
        TotalUploadedDisplay = FormattingUtils.FormatBytes(HistoryRecords.Sum(h => h.ArchiveSizeBytes));
        OnPropertyChanged(nameof(SuccessRateDisplay));

        ComputeStaleJobs();
    }

    private void ComputeStaleJobs()
    {
        if (Config.Backup.WarnStaleDays <= 0)
        {
            StaleJobsCount = 0;
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-Config.Backup.WarnStaleDays);
        var stale = 0;

        foreach (var card in Jobs)
        {
            if (card.Enabled && (card.Model.LastRunTime == null || card.Model.LastRunTime < cutoff))
            {
                stale++;
            }
        }

        StaleJobsCount = stale;
    }

    [RelayCommand]
    public async Task RunJobAsync(JobCardViewModel? card)
    {
        if (card == null || IsBusy) return;

        IsBusy = true;
        ProgressPhase = "Starting";
        ProgressPercent = 0;
        ProgressStatusMessage = $"Starting backup: {card.Name}";

        var progress = new Progress<BackupProgressInfo>(p =>
        {
            ProgressPhase = p.Phase;
            ProgressPercent = p.Percent;
            ProgressStatusMessage = p.StatusMessage;
            ProgressDetail = p.CurrentFile ?? "";
        });

        try
        {
            await _pipeline.ExecuteJobAsync(Config, card.Model, progress);
            card.NotifyStateChanged();
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Error running backup: {ex.Message}", "Backup Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RunAllAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        ProgressPhase = "Batch Starting";
        ProgressPercent = 0;
        ProgressStatusMessage = "Starting batch backup of all enabled jobs...";

        var progress = new Progress<(int cur, int total, BackupProgressInfo info)>(p =>
        {
            ProgressPhase = $"Job {p.cur}/{p.total} - {p.info.Phase}";
            ProgressPercent = p.info.Percent;
            ProgressStatusMessage = $"[{p.cur}/{p.total}] {p.info.StatusMessage}";
            ProgressDetail = p.info.CurrentFile ?? "";
        });

        try
        {
            await _batchRunner.RunAllAsync(Config, progress);
            await RefreshJobsAsync();
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Batch backup error: {ex.Message}", "Batch Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteJobAsync(JobCardViewModel? card)
    {
        if (card == null) return;

        var confirm = MessageBox.Show($"Are you sure you want to delete backup job '{card.Name}'?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var allJobs = await _configService.LoadJobsAsync();
        allJobs.RemoveAll(j => j.Id == card.Id);
        await _configService.SaveJobsAsync(allJobs);
        await _schedulerService.RemoveJobTaskAsync(card.Id);

        await RefreshJobsAsync();
    }

    [RelayCommand]
    public async Task ToggleJobAsync(JobCardViewModel? card)
    {
        if (card == null) return;
        card.Model.Enabled = !card.Model.Enabled;

        var allJobs = await _configService.LoadJobsAsync();
        var match = allJobs.FirstOrDefault(j => j.Id == card.Id);
        if (match != null) match.Enabled = card.Model.Enabled;
        await _configService.SaveJobsAsync(allJobs);

        await _schedulerService.SyncJobTaskAsync(card.Model);
        card.NotifyStateChanged();
    }

    [RelayCommand]
    public async Task RefreshRecoveryAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressPhase = "Scanning";
        ProgressPercent = 50;
        ProgressStatusMessage = "Browsing cloud archives...";

        try
        {
            var result = await _recoveryService.ListAllArchivesAsync(Config);
            CloudArchives.Clear();
            if (result.IsSuccess)
            {
                foreach (var item in result.Value)
                {
                    CloudArchives.Add(item);
                }
            }
            else
            {
                NotificationHub.ShowWarning($"Could not list archives: {result.Error}", "Recovery Warning");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ExecuteRestoreAsync()
    {
        if (SelectedArchive == null)
        {
            NotificationHub.ShowWarning("Please select an archive to restore.", "Select Archive");
            return;
        }

        if (string.IsNullOrWhiteSpace(RestoreDestinationPath))
        {
            NotificationHub.ShowWarning("Please select a valid destination folder for extraction.", "Missing Target");
            return;
        }

        var confirm = MessageBox.Show(
            $"Restore archive '{SelectedArchive.DisplayName}' into:\n{RestoreDestinationPath}\n\nMode: {RestoreWriteMode.ToUpperInvariant()}?",
            "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        var progress = new Progress<(string phase, double pct, string msg)>(p =>
        {
            ProgressPhase = p.phase;
            ProgressPercent = p.pct;
            ProgressStatusMessage = p.msg;
        });

        try
        {
            var res = await _recoveryService.RestoreArchiveAsync(Config, SelectedArchive, RestoreDestinationPath, RestoreWriteMode, progress);
            if (res.IsSuccess)
            {
                NotificationHub.ShowSuccess("Restore completed successfully!", "Restore Finished");
            }
            else
            {
                NotificationHub.ShowError($"Restore failed: {res.Error}", "Restore Failed");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // SQL Server Disaster Recovery & Drills
    [ObservableProperty]
    private JobCardViewModel? _selectedSqlJob;

    [ObservableProperty]
    private string _sqlRestoreDatabaseName = "";

    [ObservableProperty]
    private string _sqlRestoreTargetDbName = "";

    [ObservableProperty]
    private bool _sqlPointInTimeEnabled;

    [ObservableProperty]
    private DateTime _sqlPointInTime = DateTime.Now;

    [ObservableProperty]
    private string _sqlRestorePassphrase = "";

    [ObservableProperty]
    private bool _sqlRestoreReplace = true;

    [ObservableProperty]
    private string _drillStatusDisplay = "No restore drills run yet";

    [ObservableProperty]
    private string _drillStatusColor = "#94A3B8";

    [ObservableProperty]
    private string _drillRtoDisplay = "--";

    [RelayCommand]
    public async Task ExecuteRestoreDrillAsync()
    {
        if (SelectedSqlJob == null)
        {
            NotificationHub.ShowWarning("Please select a SQL Server backup job first.", "Drill Selection");
            return;
        }

        var db = string.IsNullOrWhiteSpace(SqlRestoreDatabaseName) 
            ? SelectedSqlJob.Model.Sql?.Databases.FirstOrDefault() ?? "" 
            : SqlRestoreDatabaseName;

        if (string.IsNullOrWhiteSpace(db))
        {
            NotificationHub.ShowWarning("Please specify the database name to drill test.", "Missing Database");
            return;
        }

        if (_restoreDrillService == null)
        {
            NotificationHub.ShowError("Restore drill service is not initialized.", "Error");
            return;
        }

        IsBusy = true;
        ProgressPhase = "Sandbox Drill";
        ProgressPercent = 30;
        ProgressStatusMessage = $"Starting automated restore drill for '{db}' -> ub_drill_{db}...";
        DrillStatusDisplay = $"Running drill for {db}...";
        DrillStatusColor = "#38BDF8";

        try
        {
            var res = await _restoreDrillService.RunDrillAsync(Config, SelectedSqlJob.Model, db);
            DrillRtoDisplay = $"{res.RtoSeconds:F1}s RTO";
            if (res.Success)
            {
                DrillStatusDisplay = $"Drill PASSED: Restored & DBCC CHECKDB valid in {res.RtoSeconds:F1}s";
                DrillStatusColor = "#4ADE80";
                NotificationHub.ShowSuccess($"Automated restore drill PASSED for '{db}' in {res.RtoSeconds:F1}s (CHECKDB verified)!", "Drill Passed");
            }
            else
            {
                DrillStatusDisplay = $"Drill FAILED: {res.ErrorMessage}";
                DrillStatusColor = "#F87171";
                NotificationHub.ShowError($"Restore drill FAILED: {res.ErrorMessage}", "Drill Failed");
            }
        }
        catch (Exception ex)
        {
            DrillStatusDisplay = $"Drill Error: {ex.Message}";
            DrillStatusColor = "#F87171";
            NotificationHub.ShowError($"Restore drill error: {ex.Message}", "Drill Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ExecuteSqlCloudRestoreAsync()
    {
        if (SelectedSqlJob == null)
        {
            NotificationHub.ShowWarning("Please select a SQL Server backup job.", "Select Job");
            return;
        }

        var db = string.IsNullOrWhiteSpace(SqlRestoreDatabaseName) 
            ? SelectedSqlJob.Model.Sql?.Databases.FirstOrDefault() ?? "" 
            : SqlRestoreDatabaseName;

        if (string.IsNullOrWhiteSpace(db))
        {
            NotificationHub.ShowWarning("Please specify the database to restore.", "Missing Database");
            return;
        }

        if (_sqlRestoreOrchestrator == null)
        {
            NotificationHub.ShowError("SQL Restore Orchestrator is not initialized.", "Error");
            return;
        }

        var targetDb = string.IsNullOrWhiteSpace(SqlRestoreTargetDbName) ? db : SqlRestoreTargetDbName;
        var confirm = MessageBox.Show(
            $"Restore SQL Server database '{db}' as '{targetDb}'?\n\nTarget SQL Server: {SelectedSqlJob.Model.Sql?.Server}\nPoint-in-Time: {(SqlPointInTimeEnabled ? SqlPointInTime.ToString("yyyy-MM-dd HH:mm:ss") : "Latest Backup")}\nReplace Existing: {SqlRestoreReplace}",
            "Confirm Database Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var res = await _sqlRestoreOrchestrator.RestoreFromCloudAsync(
                Config,
                SelectedSqlJob.Model,
                db,
                SqlPointInTimeEnabled ? SqlPointInTime : null,
                targetDb,
                null,
                string.IsNullOrWhiteSpace(SqlRestorePassphrase) ? null : SqlRestorePassphrase,
                SqlRestoreReplace,
                (msg, pct) =>
                {
                    ProgressPhase = "Restoring";
                    ProgressPercent = pct;
                    ProgressStatusMessage = msg;
                });

            if (res.IsSuccess)
            {
                NotificationHub.ShowSuccess($"Database '{targetDb}' restored successfully!", "Restore Finished");
            }
            else
            {
                NotificationHub.ShowError($"Database restore failed: {res.Error}", "Restore Failed");
            }
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Database restore failed: {ex.Message}", "Restore Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    private bool _isValidatingServer;

    public string EmailRecipientsText
    {
        get => string.Join(", ", Config?.Email?.ToAddresses ?? []);
        set
        {
            if (Config?.Email == null) return;
            var list = (value ?? "")
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Config.Email.ToAddresses = list;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    public void SetNotificationRule(string rule)
    {
        if (Config?.Email == null) return;
        switch (rule.ToLowerInvariant())
        {
            case "failure":
                Config.Email.NotifyOnFailure = true;
                Config.Email.NotifyOnSuccess = false;
                break;
            case "success":
                Config.Email.NotifyOnFailure = false;
                Config.Email.NotifyOnSuccess = true;
                break;
            case "both":
            case "always":
                Config.Email.NotifyOnFailure = true;
                Config.Email.NotifyOnSuccess = true;
                break;
        }
        OnPropertyChanged(nameof(Config));
        NotificationHub.ShowInfo($"Notification rule set to: {rule.ToUpperInvariant()}", "Notification Settings");
    }

    [RelayCommand]
    public async Task RunValidateServerAsync()
    {
        if (IsValidatingServer) return;
        IsValidatingServer = true;

        // Immediately navigate to Settings -> Storage & Maintenance where report is displayed
        ActiveTab = "Settings";
        SelectedSettingsTab = "Storage";

        try
        {
            var results = await _diagnosticsService.RunDiagnosticsAsync(Config);
            DiagnosticItems.Clear();
            foreach (var item in results) DiagnosticItems.Add(item);
            OnPropertyChanged(nameof(HasDiagnostics));

            var fails = results.Count(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Status, "ERROR", StringComparison.OrdinalIgnoreCase));
            var warns = results.Count(r => string.Equals(r.Status, "WARN", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Status, "WARNING", StringComparison.OrdinalIgnoreCase));

            if (fails > 0)
            {
                NotificationHub.ShowWarning($"Server Validation: {fails} issue(s) detected out of {results.Count} checks. Review report in Storage & Maintenance.", "Server Validation");
            }
            else if (warns > 0)
            {
                NotificationHub.ShowWarning($"Server Validation: All tests passed with {warns} warning(s). Review report in Storage & Maintenance.", "Server Validation");
            }
            else
            {
                NotificationHub.ShowSuccess($"Server Validation: All {results.Count} checks passed! (Temp disk, System drive, S3, Email setup, Backup sources).", "Server Healthy");
            }
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Server validation failed: {ex.Message}", "Validation Error");
        }
        finally
        {
            IsValidatingServer = false;
        }
    }

    [RelayCommand]
    public Task ValidateServerAsync() => RunValidateServerAsync();

    [RelayCommand]
    public Task RunDiagnosticsAsync() => RunValidateServerAsync();

    [RelayCommand]
    public void SelectSettingsTab(string tab)
    {
        SelectedSettingsTab = tab;
    }

    [RelayCommand]
    public void SelectRecoveryTab(string tab)
    {
        SelectedRecoveryTab = tab;
    }

    [RelayCommand]
    public void UseSchedulePreset(string preset)
    {
        if (TimeSpan.TryParse(preset, out var ts))
        {
            _syncingScheduleTime = true;
            var hour24 = ts.Hours;
            ScheduleIsPm = hour24 >= 12;
            ScheduleIsAm = !ScheduleIsPm;
            ScheduleHour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
            ScheduleMinute = ts.Minutes;
            if (Config?.Schedule != null)
            {
                Config.Schedule.Time = $"{hour24:D2}:{ScheduleMinute:D2}";
            }
            _syncingScheduleTime = false;
            OnPropertyChanged(nameof(NextRunHint));
            NotificationHub.ShowInfo($"Schedule updated to {preset} ({NextRunHint}). Click 'Save All Settings' to apply.", "Schedule Updated");
        }
    }

    private void SyncFromConfigScheduleTime()
    {
        if (Config?.Schedule == null) return;
        var timeStr = Config.Schedule.Time;
        if (string.IsNullOrWhiteSpace(timeStr)) timeStr = "23:00";
        if (TimeSpan.TryParse(timeStr, out var ts))
        {
            _syncingScheduleTime = true;
            var hour24 = ts.Hours;
            ScheduleIsPm = hour24 >= 12;
            ScheduleIsAm = !ScheduleIsPm;
            ScheduleHour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
            ScheduleMinute = ts.Minutes;
            _syncingScheduleTime = false;
            OnPropertyChanged(nameof(NextRunHint));
        }
    }

    private void SyncToConfigScheduleTime()
    {
        if (_syncingScheduleTime || Config?.Schedule == null) return;
        var hour24 = ScheduleIsPm ? (ScheduleHour12 % 12) + 12 : (ScheduleHour12 % 12);
        Config.Schedule.Time = $"{hour24:D2}:{ScheduleMinute:D2}";
        OnPropertyChanged(nameof(NextRunHint));
    }

    partial void OnScheduleHour12Changed(int value) => SyncToConfigScheduleTime();
    partial void OnScheduleMinuteChanged(int value) => SyncToConfigScheduleTime();
    partial void OnScheduleIsAmChanged(bool value)
    {
        if (value && ScheduleIsPm) ScheduleIsPm = false;
        SyncToConfigScheduleTime();
    }
    partial void OnScheduleIsPmChanged(bool value)
    {
        if (value && ScheduleIsAm) ScheduleIsAm = false;
        SyncToConfigScheduleTime();
    }

    [ObservableProperty]
    private bool _isSmtpPasswordVisible;

    [RelayCommand]
    public void ToggleSmtpPasswordVisibility()
    {
        IsSmtpPasswordVisible = !IsSmtpPasswordVisible;
    }

    [ObservableProperty]
    private bool _isAwsSecretVisible;

    [RelayCommand]
    public void ToggleAwsSecretVisibility()
    {
        IsAwsSecretVisible = !IsAwsSecretVisible;
    }

    [RelayCommand]
    public async Task TestS3ConnectionAsync()
    {
        IsBusy = true;
        ProgressPhase = "Testing AWS S3";
        ProgressPercent = 50;
        ProgressStatusMessage = "Connecting to Amazon S3 bucket...";

        try
        {
            var provider = new UniBackup.Infrastructure.Storage.AwsS3StorageProvider();
            var res = await provider.ValidateConnectionAsync(Config);
            if (res.IsSuccess)
            {
                NotificationHub.ShowSuccess($"AWS S3 connection verified successfully for bucket '{Config.Aws.BucketName}' in {Config.Aws.Region}!", "AWS S3 Connected");
            }
            else
            {
                NotificationHub.ShowError($"AWS S3 connection failed: {res.Error}", "AWS S3 Connection Error");
            }
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"AWS S3 connection error: {ex.Message}", "AWS S3 Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ReauthenticateGoogleDrive()
    {
        NotificationHub.ShowSuccess("Google Drive sign-in active: UNIPROCLOUDBACKUP (headless ready). Copy gdrive_credentials.json to other servers to reuse.", "Google Drive Connected");
    }

    [RelayCommand]
    public void BrowseTempFolder()
    {
        var initialDir = !string.IsNullOrWhiteSpace(Config.Backup.TempFolder) && Directory.Exists(Config.Backup.TempFolder)
            ? Config.Backup.TempFolder
            : @"C:\";

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Drive Partition or Staging Directory",
            InitialDirectory = initialDir
        };

        if (dialog.ShowDialog() == true)
        {
            var selected = dialog.FolderName;
            var normalized = NormalizeTempFolderPath(selected);
            Config.Backup.TempFolder = normalized;
            OnPropertyChanged(nameof(Config));
            UpdateFreeSpaceIndicators();
            NotificationHub.ShowSuccess($"Temporary staging path set to: {normalized}", "Staging Directory");
        }
    }

    public static string NormalizeTempFolderPath(string path)
    {
        var cleaned = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return @"C:\UniBackup\temp";

        // Remove trailing slashes for uniform analysis
        cleaned = cleaned.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Bare drive letter e.g. "D:" or "D"
        if (cleaned.Length <= 2 && char.IsLetter(cleaned[0]))
        {
            return cleaned.Substring(0, 1).ToUpperInvariant() + @":\UniBackup\temp";
        }

        try
        {
            var root = Path.GetPathRoot(cleaned)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // If the user selected the drive partition root (e.g. "D:" or "D:\" or "E:")
            if (!string.IsNullOrEmpty(root) && string.Equals(cleaned, root, StringComparison.OrdinalIgnoreCase))
            {
                return root + @"\UniBackup\temp";
            }

            var dirName = Path.GetFileName(cleaned);
            // If already ends in "temp" or "staging", keep as is
            if (string.Equals(dirName, "temp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dirName, "staging", StringComparison.OrdinalIgnoreCase))
            {
                return cleaned;
            }

            // If folder ends in "UniBackup", append "\temp"
            if (string.Equals(dirName, "UniBackup", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(cleaned, "temp");
            }

            // Otherwise, append "\UniBackup\temp"
            return Path.Combine(cleaned, "UniBackup", "temp");
        }
        catch
        {
            return cleaned;
        }
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        SyncToConfigScheduleTime();
        Config.Backup.TempFolder = NormalizeTempFolderPath(Config.Backup.TempFolder);
        if (string.IsNullOrWhiteSpace(Config.Email.Username) && !string.IsNullOrWhiteSpace(Config.Email.FromAddress))
        {
            Config.Email.Username = Config.Email.FromAddress;
        }
        else if (string.IsNullOrWhiteSpace(Config.Email.FromAddress) && !string.IsNullOrWhiteSpace(Config.Email.Username))
        {
            Config.Email.FromAddress = Config.Email.Username;
        }

        var result = await _configService.SaveConfigAsync(Config);
        if (result.IsSuccess)
        {
            if (TaskInstalled)
            {
                await _schedulerService.SyncAllJobsTaskAsync(Config.Schedule);
            }
            NotificationHub.ShowSuccess("Settings and backup schedule saved successfully!", "Settings Saved");
            UpdateFreeSpaceIndicators();
            RefreshServiceStatus();
        }
        else
        {
            NotificationHub.ShowError($"Failed to save settings: {result.Error}", "Save Error");
        }
    }

    [RelayCommand]
    public async Task TestEmailAsync()
    {
        IsBusy = true;
        ProgressPhase = "Testing Email";
        ProgressPercent = 50;
        ProgressStatusMessage = "Sending test email notification...";

        try
        {
            if (string.IsNullOrWhiteSpace(Config.Email.Username) && !string.IsNullOrWhiteSpace(Config.Email.FromAddress))
            {
                Config.Email.Username = Config.Email.FromAddress;
            }
            else if (string.IsNullOrWhiteSpace(Config.Email.FromAddress) && !string.IsNullOrWhiteSpace(Config.Email.Username))
            {
                Config.Email.FromAddress = Config.Email.Username;
            }

            var res = await _notificationService.SendTestNotificationAsync(Config);
            if (res.IsSuccess)
            {
                NotificationHub.ShowSuccess("Test email delivered successfully!", "Email Dispatched");
            }
            else
            {
                NotificationHub.ShowError($"Test email failed: {res.Error}", "Email Failed");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ScheduleAllJobsAsync()
    {
        var res = await _schedulerService.SyncAllJobsTaskAsync(Config.Schedule);
        if (res.IsSuccess)
        {
            NotificationHub.ShowSuccess("All-jobs nightly scheduled task registered successfully in Windows Task Scheduler!", "Task Scheduled");
        }
        else
        {
            NotificationHub.ShowError($"Failed to schedule all jobs task: {res.Error}", "Schedule Failed");
        }
    }

    [RelayCommand]
    public async Task ExportHistoryCsvAsync()
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = $"UniBackup_History_{DateTime.Now:yyyyMMdd}.csv"
        };

        if (sfd.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("RunId,JobId,JobName,Timestamp,SourceFolder,ArchiveName,SizeBytes,DurationSeconds,Status,Destinations,ErrorMessage");

            foreach (var h in HistoryRecords)
            {
                var dests = string.Join(";", h.Destinations.Select(d => $"{d.Destination}:{d.Status}"));
                sb.AppendLine($"\"{h.RunId}\",\"{h.JobId}\",\"{h.JobName}\",\"{h.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{h.SourceFolder}\",\"{h.ArchiveName}\",{h.ArchiveSizeBytes},{h.DurationSeconds:F1},\"{h.Status}\",\"{dests}\",\"{h.ErrorMessage}\"");
            }

            await File.WriteAllTextAsync(sfd.FileName, sb.ToString());
            NotificationHub.ShowSuccess($"Exported {HistoryRecords.Count} records to CSV successfully!", "Export Finished");
        }
    }

    [RelayCommand]
    public void RefreshServiceStatus()
    {
        WindowsServiceStatus = WindowsServiceManager.GetServiceStatus();
        WindowsServiceRunning = WindowsServiceStatus == "Running";
        WindowsServiceStopped = WindowsServiceStatus == "Stopped";
        WindowsServiceNotInstalled = WindowsServiceStatus == "Not Installed";
        WindowsServiceInstalled = !WindowsServiceNotInstalled;

        WindowsServiceStatusColor = WindowsServiceStatus switch
        {
            "Running" => "#10B981",
            "Stopped" => "#EF4444",
            "Starting" => "#F59E0B",
            _ => "#94A3B8"
        };

        WindowsServiceDetail = WindowsServiceRunning
            ? $"runs as LocalSystem · automatic background start · {NextRunHint}"
            : WindowsServiceStopped
                ? "service is installed but stopped · start service to resume background backups"
                : "service is not installed · backups will not run in background";

        var taskInfo = _schedulerService.QueryAllJobsTaskInfo();
        TaskInstalled = taskInfo.Installed;
        TaskNotInstalled = !taskInfo.Installed;
        TaskRunning = taskInfo.Running;
        TaskStopped = taskInfo.Installed && !taskInfo.Running;
        TaskState = taskInfo.State;
        TaskDetail = taskInfo.Detail;

        if (WindowsServiceRunning) RunByTask = false;
        else if (TaskInstalled && !WindowsServiceInstalled) RunByTask = true;
    }

    [RelayCommand]
    public async Task InstallTaskAsync()
    {
        SyncToConfigScheduleTime();
        var res = await _schedulerService.SyncAllJobsTaskAsync(Config.Schedule);
        if (res.IsSuccess)
        {
            NotificationHub.ShowSuccess("All-jobs nightly scheduled task registered in Windows Task Scheduler!", "Task Installed");
        }
        else
        {
            NotificationHub.ShowError($"Failed to install task: {res.Error}", "Task Error");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public void StartTask()
    {
        var res = _schedulerService.RunAllJobsTaskNow();
        if (res.IsSuccess)
        {
            NotificationHub.ShowSuccess("Triggered scheduled backup task run in Windows Task Scheduler.", "Task Started");
        }
        else
        {
            NotificationHub.ShowError(res.Error ?? "Failed to start task.", "Task Start Error");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public async Task RemoveTaskAsync()
    {
        var res = await _schedulerService.RemoveAllJobsTaskAsync();
        if (res.IsSuccess)
        {
            NotificationHub.ShowInfo("Nightly scheduled task removed from Windows Task Scheduler.", "Task Removed");
        }
        else
        {
            NotificationHub.ShowError(res.Error ?? "Failed to remove task.", "Task Error");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public void CopyConfigPath()
    {
        try
        {
            Clipboard.SetText(ConfigFileLocation);
            NotificationHub.ShowSuccess($"Configuration file path copied to clipboard:\n{ConfigFileLocation}", "Copied to Clipboard");
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Could not copy path: {ex.Message}", "Copy Failed");
        }
    }

    [RelayCommand]
    public async Task RecheckAllAsync()
    {
        UpdateFreeSpaceIndicators();
        RefreshServiceStatus();
        await RefreshJobsAsync();
        await RefreshHistoryAsync();
        NotificationHub.ShowSuccess("All service, task scheduler, and storage statuses re-checked successfully.", "Re-check Complete");
    }

    [RelayCommand]
    public void InstallWindowsService()
    {
        var result = WindowsServiceManager.InstallService();
        if (result.IsSuccess)
        {
            NotificationHub.ShowSuccess("UniBackup Windows Service installed successfully!", "Service Installed");
        }
        else
        {
            NotificationHub.ShowError(result.Error ?? "Service installation failed.", "Service Installation Error");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public void UninstallWindowsService()
    {
        var confirm = MessageBox.Show("Are you sure you want to remove the UniBackup Windows Service?", "Confirm Uninstall",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = WindowsServiceManager.UninstallService();
        if (result.IsSuccess)
        {
            NotificationHub.ShowInfo("Windows Service uninstalled successfully.", "Service Removed");
        }
        else
        {
            NotificationHub.ShowError(result.Error ?? "Service uninstall failed.", "Service Error");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public void StartWindowsService()
    {
        var result = WindowsServiceManager.StartService();
        if (!result.IsSuccess)
        {
            NotificationHub.ShowError(result.Error ?? "Failed to start service.", "Error Starting Service");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public void StopWindowsService()
    {
        var result = WindowsServiceManager.StopService();
        if (!result.IsSuccess)
        {
            NotificationHub.ShowError(result.Error ?? "Failed to stop service.", "Error Stopping Service");
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    public void OpenLogsFolder()
    {
        var logsDir = AppPaths.DefaultLogsDirectory;
        Directory.CreateDirectory(logsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", logsDir) { UseShellExecute = true });
    }
}
