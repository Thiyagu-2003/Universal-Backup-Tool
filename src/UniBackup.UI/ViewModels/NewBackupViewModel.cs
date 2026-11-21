using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
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
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(IsStep4))]
    private int _currentStep = 1;

    public bool CanGoPrevious => CurrentStep > 1;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlServer))]
    [NotifyPropertyChangedFor(nameof(IsFolder))]
    private string _kind = "folder"; // "folder" | "sqlserver"

    public bool IsSqlServer => Kind.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);
    public bool IsFolder => !IsSqlServer;

    // Job Common Details
    [ObservableProperty]
    private string _jobName = "";

    [ObservableProperty]
    private string _sourceFolder = "";

    [ObservableProperty]
    private string _subfolder = "";

    // SQL Server Settings
    [ObservableProperty]
    private string _sqlServer = "localhost";

    [ObservableProperty]
    private bool _sqlUseWindowsAuth = true;

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
    private string _cloudFolder = "";

    [ObservableProperty]
    private bool _useVss = true;

    [ObservableProperty]
    private bool _includeInRunAll = true;

    // Schedule Settings (Schedule v2)
    [ObservableProperty]
    private string _selectedFrequency = "daily";

    [ObservableProperty]
    private string _scheduledTime = "02:00";

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

    [ObservableProperty]
    private bool _diffEnabled = false;

    [ObservableProperty]
    private int _diffMinutes = 240; // Every 4 hours

    [ObservableProperty]
    private bool _logEnabled = false;

    [ObservableProperty]
    private int _logMinutes = 15; // Every 15 minutes

    [ObservableProperty]
    private bool _windowEnabled = false;

    [ObservableProperty]
    private string _windowStart = "08:00";

    [ObservableProperty]
    private string _windowEnd = "20:00";

    // Options (Retention, Encryption, Object Lock)
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
    private bool _objectLockEnabled = false;

    [ObservableProperty]
    private int _objectLockDays = 30;

    [ObservableProperty]
    private ObservableCollection<IisSiteInfo> _iisSites = [];

    [ObservableProperty]
    private IisSiteInfo? _selectedIisSite;

    [ObservableProperty]
    private bool _isIisLoaded;

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; private set; }

    public NewBackupViewModel(
        ConfigurationService configService,
        IIisDiscoveryService iisService,
        ISchedulerService schedulerService)
    {
        _configService = configService;
        _iisService = iisService;
        _schedulerService = schedulerService;
    }

    [RelayCommand]
    public void NextStep()
    {
        if (CurrentStep < 4)
        {
            CurrentStep++;
        }
    }

    [RelayCommand]
    public void PreviousStep()
    {
        if (CurrentStep > 1)
        {
            CurrentStep--;
        }
    }

    [RelayCommand]
    public void SetStep(int step)
    {
        if (step >= 1 && step <= 4)
        {
            CurrentStep = step;
        }
    }

    [RelayCommand]
    public async Task TestSqlConnectionAsync()
    {
        IsTestingConnection = true;
        SqlConnectionStatus = "Testing connection to " + SqlServer + "...";
        try
        {
            var crypto = new CredentialProtector();
            var sqlService = new SqlServerBackupService(crypto);
            var source = new SqlServerSource
            {
                Server = SqlServer,
                UseWindowsAuth = SqlUseWindowsAuth,
                Username = SqlUseWindowsAuth ? null : SqlUsername,
                PasswordSealed = SqlUseWindowsAuth ? null : crypto.Seal(SqlPassword),
                TrustServerCertificate = SqlTrustServerCert
            };

            var dbs = await sqlService.ListDatabasesAsync(source, CancellationToken.None);
            if (dbs.IsSuccess)
            {
                AvailableDatabases.Clear();
                foreach (var db in dbs.Value)
                {
                    AvailableDatabases.Add(db.Name);
                }
                SqlConnectionStatus = $"Connected! Found {dbs.Value.Count} database(s).";
                if (string.IsNullOrWhiteSpace(JobName))
                {
                    JobName = $"SQL_{SqlServer.Replace('\\', '_')}";
                }
            }
            else
            {
                SqlConnectionStatus = "Connection failed: " + dbs.Error;
            }
        }
        catch (Exception ex)
        {
            SqlConnectionStatus = "Error: " + ex.Message;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    public void BrowseSourceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Source Folder to Backup"
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
    public async Task LoadIisSitesAsync()
    {
        var result = await _iisService.DiscoverSitesAsync();
        IisSites.Clear();

        if (result.IsSuccess)
        {
            foreach (var site in result.Value)
            {
                IisSites.Add(site);
            }
            IsIisLoaded = true;
        }
        else
        {
            MessageBox.Show(result.Error, "IIS Discovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    partial void OnSelectedIisSiteChanged(IisSiteInfo? value)
    {
        if (value != null)
        {
            JobName = value.Name;
            SourceFolder = value.PhysicalPath;
        }
    }

    [RelayCommand]
    public async Task SaveJobAsync()
    {
        if (IsFolder)
        {
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                MessageBox.Show("Please select a valid source folder.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(SourceFolder))
            {
                var res = MessageBox.Show($"Source folder does not exist on disk:\n{SourceFolder}\n\nDo you still want to save this job?",
                    "Missing Folder", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(SqlServer))
            {
                MessageBox.Show("Please specify a valid SQL Server instance.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var destinations = new List<string>();
        if (EnableS3) destinations.Add("s3");
        if (EnableGDrive) destinations.Add("gdrive");

        if (destinations.Count == 0)
        {
            MessageBox.Show("Please enable at least one destination (S3 or Google Drive).", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
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
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(JobName) ? (IsSqlServer ? $"SQL_{SqlServer}" : Path.GetFileName(SourceFolder)) : JobName,
            Kind = Kind,
            SourceFolder = IsFolder ? SourceFolder : "",
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

        var jobs = await _configService.LoadJobsAsync();
        jobs.Add(job);
        await _configService.SaveJobsAsync(jobs);
        await _schedulerService.SyncJobTaskAsync(job);

        DialogResult = true;
        CloseAction?.Invoke();
    }
}
