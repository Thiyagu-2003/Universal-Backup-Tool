using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.UI.Dialogs;
using UniBackup.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniBackup.UI.ViewModels;

public partial class NewBackupViewModel : ObservableObject
{
    private readonly ConfigurationService _configService;
    private readonly IIisDiscoveryService _iisService;
    private readonly ISchedulerService _schedulerService;

    // Stepped Wizard (1: Source, 2: Schedule, 3: Destinations, 4: Options)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(IsStep4))]
    private int _currentStep = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(WizardPageTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private string? _editingJobId;

    public bool IsEditing => !string.IsNullOrEmpty(EditingJobId);
    public string WizardPageTitle => IsEditing ? "Edit backup job" : "Create backup job";
    public string SaveButtonText => IsEditing ? "Update job" : "Save job";

    public bool CanGoPrevious => CurrentStep > 1;
    public bool CanGoNext => CurrentStep < 4;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;

    [ObservableProperty]
    private string _kind = "folder"; // "folder" | "sqlserver" | "iis"

    public bool IsSqlServer
    {
        get => Kind.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value) Kind = "sqlserver";
            else if (Kind == "sqlserver") Kind = "folder";
            NotifyKindProps();
        }
    }

    public bool IsIisSite
    {
        get => Kind.Equals("iis", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value) Kind = "iis";
            else if (Kind == "iis") Kind = "folder";
            NotifyKindProps();
        }
    }

    public bool IsFolder
    {
        get => !IsSqlServer && !IsIisSite;
        set
        {
            if (value) Kind = "folder";
            else if (Kind == "folder") Kind = "sqlserver";
            NotifyKindProps();
        }
    }

    private void NotifyKindProps()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsSqlServer));
        OnPropertyChanged(nameof(IsIisSite));
        OnPropertyChanged(nameof(IsFolder));
        UpdateLiveScheduleSummary();
    }

    // Inline Validation (§4.3, §12)
    [ObservableProperty]
    private string _validationError = "";

    [ObservableProperty]
    private bool _hasValidationError;

    // Job Common Details
    [ObservableProperty]
    private string _jobName = "";

    [ObservableProperty]
    private string _sourceFolder = "";

    [ObservableProperty]
    private string _subfolder = "";

    // IIS Selection
    [ObservableProperty]
    private ObservableCollection<IisSiteVm> _selectedIisSites = [];

    [ObservableProperty]
    private string _iisSummaryDisplay = "0 sites · 0 B before compression";

    [ObservableProperty]
    private bool _exportIisConfiguration = true;

    // SQL Server Settings
    [ObservableProperty]
    private string _sqlServer = "localhost";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SqlUseSqlAuth))]
    private bool _sqlUseWindowsAuth = true;

    public bool SqlUseSqlAuth
    {
        get => !SqlUseWindowsAuth;
        set
        {
            SqlUseWindowsAuth = !value;
            OnPropertyChanged(nameof(SqlUseSqlAuth));
            OnPropertyChanged(nameof(SqlUseWindowsAuth));
        }
    }

    [ObservableProperty]
    private string _sqlUsername = "";

    [ObservableProperty]
    private string _sqlPassword = "";

    [ObservableProperty]
    private bool _sqlTrustServerCert = true;

    [ObservableProperty]
    private bool _sqlAllDatabases = true;

    [ObservableProperty]
    private string _sqlExcludePatterns = "";

    [ObservableProperty]
    private bool _sqlNativeCompression = true;

    [ObservableProperty]
    private bool _sqlChecksum = true;

    [ObservableProperty]
    private bool _sqlVerify = true;

    [ObservableProperty]
    private bool _sqlRunCheckDb = false;

    [ObservableProperty]
    private bool _sqlDirectToUrl = false;

    [ObservableProperty]
    private string _sqlConnectionStatus = "";

    [ObservableProperty]
    private bool _isTestingConnection = false;

    [ObservableProperty]
    private ObservableCollection<string> _availableDatabases = [];

    // Destinations
    [ObservableProperty]
    private bool _enableS3 = true;

    [ObservableProperty]
    private bool _enableGDrive = false;

    [ObservableProperty]
    private bool _enableAzure = false;

    [ObservableProperty]
    private bool _enableLocal = false;

    [ObservableProperty]
    private bool _enableFtp = false;

    [ObservableProperty]
    private bool _enableSftp = false;

    [ObservableProperty]
    private bool _enableWebDav = false;

    [ObservableProperty]
    private bool _enableDropbox = false;

    [ObservableProperty]
    private bool _enableOneDrive = false;

    [ObservableProperty]
    private bool _enableBox = false;

    [ObservableProperty]
    private string _cloudFolder = "";

    [ObservableProperty]
    private bool _useVss = true;

    [ObservableProperty]
    private bool _includeInRunAll = true;

    // Schedule Settings (Schedule v2)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedDays))]
    private string _selectedFrequency = "daily"; // "daily" | "selected_days" | "manual"

    public bool IsSelectedDays => SelectedFrequency == "selected_days";

    [ObservableProperty]
    private string _scheduledTime = "01:00";

    [ObservableProperty]
    private bool _monday = true;

    [ObservableProperty]
    private bool _tuesday = true;

    [ObservableProperty]
    private bool _wednesday = true;

    [ObservableProperty]
    private bool _thursday = true;

    [ObservableProperty]
    private bool _friday = true;

    [ObservableProperty]
    private bool _saturday = false;

    [ObservableProperty]
    private bool _sunday = false;

    partial void OnSelectedFrequencyChanged(string value) => UpdateLiveScheduleSummary();
    partial void OnScheduledTimeChanged(string value) => UpdateLiveScheduleSummary();
    partial void OnMondayChanged(bool value) => UpdateLiveScheduleSummary();
    partial void OnTuesdayChanged(bool value) => UpdateLiveScheduleSummary();
    partial void OnWednesdayChanged(bool value) => UpdateLiveScheduleSummary();
    partial void OnThursdayChanged(bool value) => UpdateLiveScheduleSummary();
    partial void OnFridayChanged(bool value) => UpdateLiveScheduleSummary();
    partial void OnSaturdayChanged(bool value) => UpdateLiveScheduleSummary();
    partial void OnSundayChanged(bool value) => UpdateLiveScheduleSummary();

    // SQL Sub-intervals (Diff / Log)
    [ObservableProperty]
    private bool _diffEnabled = true;

    [ObservableProperty]
    private int _diffMinutes = 240; // 4 hours

    [ObservableProperty]
    private bool _logEnabled = true;

    [ObservableProperty]
    private int _logMinutes = 15; // 15 mins

    [ObservableProperty]
    private bool _windowEnabled = false;

    [ObservableProperty]
    private string _windowStart = "08:00";

    [ObservableProperty]
    private string _windowEnd = "20:00";

    [ObservableProperty]
    private string _liveScheduleSummary = "Full backup every day at 01:00.";

    // Retention & Encryption (Step 4)
    [ObservableProperty]
    private int _retentionDays = 30;

    [ObservableProperty]
    private bool _gfsEnabled = false;

    [ObservableProperty]
    private int _gfsDailies = 7;

    [ObservableProperty]
    private int _gfsWeeklies = 4;

    [ObservableProperty]
    private int _gfsMonthlies = 12;

    [ObservableProperty]
    private bool _encryptionEnabled = false;

    [ObservableProperty]
    private string _encryptionPassphrase = "";

    [ObservableProperty]
    private string _encryptionPassphraseConfirm = "";

    [ObservableProperty]
    private bool _objectLockEnabled = false;

    [ObservableProperty]
    private int _objectLockDays = 30;

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; set; }

    public NewBackupViewModel(
        ConfigurationService configService,
        IIisDiscoveryService iisService,
        ISchedulerService schedulerService)
    {
        _configService = configService;
        _iisService = iisService;
        _schedulerService = schedulerService;
        UpdateLiveScheduleSummary();
    }

    [RelayCommand]
    public void SetStep(object? stepObj)
    {
        int step = 0;
        if (stepObj is int i) step = i;
        else if (stepObj is string s && int.TryParse(s, out var parsed)) step = parsed;

        if (step >= 1 && step <= 4)
        {
            ClearValidation();
            CurrentStep = step;
        }
    }

    [RelayCommand]
    public void NextStep()
    {
        if (ValidateCurrentStep())
        {
            ClearValidation();
            if (CurrentStep < 4)
            {
                CurrentStep++;
            }
            else
            {
                _ = SaveJobAsync();
            }
        }
    }

    [RelayCommand]
    public void PreviousStep()
    {
        ClearValidation();
        if (CurrentStep > 1)
        {
            CurrentStep--;
        }
    }

    public bool ValidateCurrentStep()
    {
        ClearValidation();

        if (CurrentStep == 1)
        {
            if (string.IsNullOrWhiteSpace(JobName))
            {
                SetValidationError("Enter a job name to identify this backup.");
                return false;
            }

            if (IsFolder)
            {
                if (string.IsNullOrWhiteSpace(SourceFolder))
                {
                    SetValidationError(@"Enter a full source path, like D:\Data or \\server\share.");
                    return false;
                }
            }
            else if (IsIisSite)
            {
                if (SelectedIisSites.Count == 0)
                {
                    SetValidationError("Select at least one IIS website from the picker dialog.");
                    return false;
                }
            }
            else if (IsSqlServer)
            {
                if (string.IsNullOrWhiteSpace(SqlServer))
                {
                    SetValidationError(@"Enter a SQL Server instance name, like localhost or SRV-DB01\SQL2022.");
                    return false;
                }
            }
        }
        else if (CurrentStep == 3)
        {
            var hasDest = EnableS3 || EnableGDrive || EnableAzure || EnableLocal ||
                          EnableFtp || EnableSftp || EnableWebDav || EnableDropbox ||
                          EnableOneDrive || EnableBox;
            if (!hasDest)
            {
                SetValidationError("Select at least one destination storage provider.");
                return false;
            }
        }
        else if (CurrentStep == 4)
        {
            if (EncryptionEnabled)
            {
                if (string.IsNullOrWhiteSpace(EncryptionPassphrase))
                {
                    SetValidationError("Enter an AES-256 encryption passphrase.");
                    return false;
                }
                if (EncryptionPassphrase != EncryptionPassphraseConfirm)
                {
                    SetValidationError("The passphrases do not match. Re-enter passphrase to confirm.");
                    return false;
                }
            }
        }

        return true;
    }

    private void SetValidationError(string msg)
    {
        ValidationError = msg;
        HasValidationError = true;
    }

    private void ClearValidation()
    {
        ValidationError = "";
        HasValidationError = false;
    }

    public void UpdateLiveScheduleSummary()
    {
        var summary = $"Full backup every day at {ScheduledTime}";
        if (SelectedFrequency == "selected_days")
        {
            var daysList = new List<string>();
            if (Monday) daysList.Add("Mon");
            if (Tuesday) daysList.Add("Tue");
            if (Wednesday) daysList.Add("Wed");
            if (Thursday) daysList.Add("Thu");
            if (Friday) daysList.Add("Fri");
            if (Saturday) daysList.Add("Sat");
            if (Sunday) daysList.Add("Sun");
            summary = $"Full backup on {string.Join(", ", daysList)} at {ScheduledTime}";
        }

        if (IsSqlServer)
        {
            if (DiffEnabled)
            {
                summary += $", then a differential every {DiffMinutes / 60} hours";
            }
            if (LogEnabled)
            {
                summary += $", and a log backup every {LogMinutes} minutes.";
            }
            else
            {
                summary += ".";
            }
        }
        else
        {
            summary += ".";
        }

        LiveScheduleSummary = summary;
    }

    [RelayCommand]
    public void OpenIisPicker()
    {
        var dialog = new IisPickerDialog
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedIisSites.Clear();
            foreach (var site in dialog.SelectedSites)
            {
                SelectedIisSites.Add(site);
            }

            ExportIisConfiguration = dialog.ExportConfiguration;
            UpdateIisSelectionSummary();

            if (SelectedIisSites.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(JobName))
                {
                    JobName = SelectedIisSites.Count == 1 ? SelectedIisSites[0].Name : $"{SelectedIisSites.Count} IIS sites";
                }
                SourceFolder = $"IIS:{string.Join(";", SelectedIisSites.Select(s => s.Name))}";
            }
        }
    }

    [RelayCommand]
    public void RemoveIisSite(IisSiteVm site)
    {
        SelectedIisSites.Remove(site);
        UpdateIisSelectionSummary();
        if (SelectedIisSites.Count == 0)
        {
            SourceFolder = "";
        }
        else
        {
            SourceFolder = $"IIS:{string.Join(";", SelectedIisSites.Select(s => s.Name))}";
        }
    }

    private void UpdateIisSelectionSummary()
    {
        var count = SelectedIisSites.Count;
        var bytes = SelectedIisSites.Sum(s => s.SizeBytes);
        IisSummaryDisplay = $"{count} {(count == 1 ? "site" : "sites")} · {FormattingUtils.FormatBytes(bytes)} before compression";
    }

    [RelayCommand]
    public void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Source Folder to Back Up"
        };
        if (dialog.ShowDialog() == true)
        {
            SourceFolder = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(JobName))
            {
                JobName = Path.GetFileName(dialog.FolderName);
            }
        }
    }

    [RelayCommand]
    public async Task TestSqlConnectionAsync()
    {
        IsTestingConnection = true;
        SqlConnectionStatus = "Testing connection...";
        try
        {
            var crypto = new CredentialProtector();
            var sqlBackup = new SqlServerBackupService(crypto);
            var src = new SqlServerSource
            {
                Server = SqlServer,
                UseWindowsAuth = SqlUseWindowsAuth,
                Username = SqlUseWindowsAuth ? null : SqlUsername,
                PasswordSealed = SqlUseWindowsAuth ? null : crypto.Seal(SqlPassword),
                TrustServerCertificate = SqlTrustServerCert
            };

            var res = await sqlBackup.ListDatabasesAsync(src, CancellationToken.None);
            if (res.IsSuccess)
            {
                AvailableDatabases.Clear();
                foreach (var db in res.Value)
                {
                    AvailableDatabases.Add(db.Name);
                }

                SqlConnectionStatus = $"Connected: {res.Value.Count} databases detected.";
            }
            else
            {
                SqlConnectionStatus = $"Connection failed: {res.Error}";
            }
        }
        catch (Exception ex)
        {
            SqlConnectionStatus = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }

    [RelayCommand]
    public async Task SaveJobAsync()
    {
        ClearValidation();

        // 1. Validate Step 1: Name and source target
        if (string.IsNullOrWhiteSpace(JobName))
        {
            SetValidationError("Enter a job name to identify this backup.");
            CurrentStep = 1;
            return;
        }

        if (IsFolder)
        {
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                SetValidationError(@"Enter a full source path, like D:\Data or \\server\share.");
                CurrentStep = 1;
                return;
            }
        }
        else if (IsIisSite)
        {
            if (SelectedIisSites.Count == 0 && string.IsNullOrWhiteSpace(SourceFolder))
            {
                SetValidationError("Select at least one IIS website from the picker dialog.");
                CurrentStep = 1;
                return;
            }
        }
        else if (IsSqlServer)
        {
            if (string.IsNullOrWhiteSpace(SqlServer))
            {
                SetValidationError(@"Enter a SQL Server instance name, like localhost or SRV-DB01\SQL2022.");
                CurrentStep = 1;
                return;
            }
        }

        // 2. Validate Step 3: Destinations
        var destinations = new List<string>();
        if (EnableS3) destinations.Add("s3");
        if (EnableGDrive) destinations.Add("gdrive");
        if (EnableAzure) destinations.Add("azure");
        if (EnableLocal) destinations.Add("local");
        if (EnableFtp) destinations.Add("ftp");
        if (EnableSftp) destinations.Add("sftp");
        if (EnableWebDav) destinations.Add("webdav");
        if (EnableDropbox) destinations.Add("dropbox");
        if (EnableOneDrive) destinations.Add("onedrive");
        if (EnableBox) destinations.Add("box");

        if (destinations.Count == 0)
        {
            SetValidationError("Select at least one destination storage provider.");
            CurrentStep = 3;
            return;
        }

        // 3. Validate Step 4: Encryption
        if (EncryptionEnabled)
        {
            if (string.IsNullOrWhiteSpace(EncryptionPassphrase))
            {
                SetValidationError("Enter an AES-256 encryption passphrase.");
                CurrentStep = 4;
                return;
            }
            if (EncryptionPassphrase != EncryptionPassphraseConfirm)
            {
                SetValidationError("The passphrases do not match. Re-enter passphrase to confirm.");
                CurrentStep = 4;
                return;
            }
        }

        var days = new List<string>();
        if (Monday) days.Add("MON");
        if (Tuesday) days.Add("TUE");
        if (Wednesday) days.Add("WED");
        if (Thursday) days.Add("THU");
        if (Friday) days.Add("FRI");
        if (Saturday) days.Add("SAT");
        if (Sunday) days.Add("SUN");

        var crypto = new CredentialProtector();
        var schedule = new JobSchedule
        {
            Freq = SelectedFrequency,
            Time = ScheduledTime,
            Days = days,
            Full = new FullBackupSchedule
            {
                Freq = SelectedFrequency,
                Time = ScheduledTime,
                Days = days
            },
            Diff = DiffEnabled ? new IntervalBackupSchedule { Enabled = true, EveryMinutes = DiffMinutes } : null,
            Log = LogEnabled ? new IntervalBackupSchedule { Enabled = true, EveryMinutes = LogMinutes } : null,
            Window = WindowEnabled ? new ScheduleRunWindow { Enabled = true, Start = WindowStart, End = WindowEnd } : null
        };

        var retentionPolicy = new RetentionPolicy
        {
            KeepDays = RetentionDays,
            GfsEnabled = GfsEnabled,
            GfsKeepDailies = GfsDailies,
            GfsKeepWeeklies = GfsWeeklies,
            GfsKeepMonthlies = GfsMonthlies,
            ObjectLockEnabled = ObjectLockEnabled,
            ObjectLockDays = ObjectLockDays
        };

        var job = new BackupJob
        {
            Id = !string.IsNullOrEmpty(EditingJobId) ? EditingJobId : Guid.NewGuid().ToString("N")[..8],
            Name = JobName,
            Kind = Kind,
            SourceFolder = SourceFolder,
            Subfolder = IsFolder && !string.IsNullOrWhiteSpace(Subfolder) ? Subfolder : null,
            Destinations = destinations,
            CloudFolder = string.IsNullOrWhiteSpace(CloudFolder) ? null : CloudFolder,
            UseVss = UseVss,
            IncludeInRunAll = IncludeInRunAll,
            Enabled = true,
            Schedule = schedule,
            Retention = retentionPolicy,
            RetentionDaysOverride = RetentionDays,
            EncryptionPassphrase = EncryptionEnabled && !string.IsNullOrWhiteSpace(EncryptionPassphrase) ? EncryptionPassphrase : null
        };

        if (IsSqlServer)
        {
            job.Sql = new SqlServerSource
            {
                Server = SqlServer,
                UseWindowsAuth = SqlUseWindowsAuth,
                Username = SqlUseWindowsAuth ? null : SqlUsername,
                PasswordSealed = SqlUseWindowsAuth ? null : crypto.Seal(SqlPassword),
                TrustServerCertificate = SqlTrustServerCert,
                AllUserDatabases = SqlAllDatabases,
                ExcludePatterns = string.IsNullOrWhiteSpace(SqlExcludePatterns)
                    ? []
                    : SqlExcludePatterns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
                NativeCompression = SqlNativeCompression,
                Checksum = SqlChecksum,
                Verify = SqlVerify,
                RunCheckDb = SqlRunCheckDb,
                DirectToUrl = SqlDirectToUrl
            };
        }

        try
        {
            var jobs = await _configService.LoadJobsAsync();
            if (!string.IsNullOrEmpty(EditingJobId))
            {
                var existingIndex = jobs.FindIndex(j => j.Id == EditingJobId);
                if (existingIndex >= 0)
                {
                    job.Pinned = jobs[existingIndex].Pinned;
                    job.Enabled = jobs[existingIndex].Enabled;
                    job.LastRunTime = jobs[existingIndex].LastRunTime;
                    job.LastRunStatus = jobs[existingIndex].LastRunStatus;
                    job.LastRunSizeBytes = jobs[existingIndex].LastRunSizeBytes;
                    job.LastRunDurationSeconds = jobs[existingIndex].LastRunDurationSeconds;
                    jobs[existingIndex] = job;
                }
                else
                {
                    jobs.Add(job);
                }
                await _configService.SaveJobsAsync(jobs);
                await _schedulerService.SyncJobTaskAsync(job);
                NotificationHub.ShowSuccess($"Updated {job.Name}.", "Job Updated");
            }
            else
            {
                jobs.Add(job);
                await _configService.SaveJobsAsync(jobs);
                await _schedulerService.SyncJobTaskAsync(job);
                NotificationHub.ShowSuccess($"Created {job.Name}. First run scheduled for {ScheduledTime}.", "Job Created");
            }

            DialogResult = true;
            CloseAction?.Invoke();
        }
        catch (Exception ex)
        {
            SetValidationError($"Failed to save backup job: {ex.Message}");
        }
    }

    public void LoadFromJob(BackupJob job)
    {
        EditingJobId = job.Id;
        JobName = job.Name;
        Kind = job.Kind;
        SourceFolder = job.SourceFolder;
        Subfolder = job.Subfolder ?? "";
        CloudFolder = job.CloudFolder ?? "";
        UseVss = job.UseVss;
        IncludeInRunAll = job.IncludeInRunAll;

        EnableS3 = job.Destinations.Any(d => d.Equals("s3", StringComparison.OrdinalIgnoreCase));
        EnableGDrive = job.Destinations.Any(d => d.Equals("gdrive", StringComparison.OrdinalIgnoreCase));
        EnableAzure = job.Destinations.Any(d => d.Equals("azure", StringComparison.OrdinalIgnoreCase));
        EnableLocal = job.Destinations.Any(d => d.Equals("local", StringComparison.OrdinalIgnoreCase));
        EnableFtp = job.Destinations.Any(d => d.Equals("ftp", StringComparison.OrdinalIgnoreCase));
        EnableSftp = job.Destinations.Any(d => d.Equals("sftp", StringComparison.OrdinalIgnoreCase));
        EnableWebDav = job.Destinations.Any(d => d.Equals("webdav", StringComparison.OrdinalIgnoreCase));
        EnableDropbox = job.Destinations.Any(d => d.Equals("dropbox", StringComparison.OrdinalIgnoreCase));
        EnableOneDrive = job.Destinations.Any(d => d.Equals("onedrive", StringComparison.OrdinalIgnoreCase));
        EnableBox = job.Destinations.Any(d => d.Equals("box", StringComparison.OrdinalIgnoreCase));

        SelectedFrequency = job.Schedule?.Freq ?? "daily";
        ScheduledTime = job.Schedule?.Time ?? "01:00";
        if (job.Schedule?.Days != null && job.Schedule.Days.Count > 0)
        {
            Monday = job.Schedule.Days.Contains("MON", StringComparer.OrdinalIgnoreCase);
            Tuesday = job.Schedule.Days.Contains("TUE", StringComparer.OrdinalIgnoreCase);
            Wednesday = job.Schedule.Days.Contains("WED", StringComparer.OrdinalIgnoreCase);
            Thursday = job.Schedule.Days.Contains("THU", StringComparer.OrdinalIgnoreCase);
            Friday = job.Schedule.Days.Contains("FRI", StringComparer.OrdinalIgnoreCase);
            Saturday = job.Schedule.Days.Contains("SAT", StringComparer.OrdinalIgnoreCase);
            Sunday = job.Schedule.Days.Contains("SUN", StringComparer.OrdinalIgnoreCase);
        }

        if (job.Schedule?.Diff != null)
        {
            DiffEnabled = job.Schedule.Diff.Enabled;
            DiffMinutes = job.Schedule.Diff.EveryMinutes;
        }
        if (job.Schedule?.Log != null)
        {
            LogEnabled = job.Schedule.Log.Enabled;
            LogMinutes = job.Schedule.Log.EveryMinutes;
        }

        RetentionDays = job.RetentionDaysOverride ?? job.Retention?.KeepDays ?? 30;

        EncryptionEnabled = !string.IsNullOrWhiteSpace(job.EncryptionPassphrase);
        EncryptionPassphrase = job.EncryptionPassphrase ?? "";

        if (job.Sql != null)
        {
            SqlServer = job.Sql.Server;
            SqlUseWindowsAuth = job.Sql.UseWindowsAuth;
            SqlUsername = job.Sql.Username ?? "";
            SqlTrustServerCert = job.Sql.TrustServerCertificate;
            SqlAllDatabases = job.Sql.AllUserDatabases;
            SqlExcludePatterns = string.Join(", ", job.Sql.ExcludePatterns);
            SqlNativeCompression = job.Sql.NativeCompression;
            SqlChecksum = job.Sql.Checksum;
            SqlVerify = job.Sql.Verify;
            SqlRunCheckDb = job.Sql.RunCheckDb;
            SqlDirectToUrl = job.Sql.DirectToUrl;
        }

        NotifyKindProps();
    }
}
