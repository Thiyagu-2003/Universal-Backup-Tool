using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.UI.Services;

namespace UniBackup.UI.ViewModels;

// =============================================================================
// SUB-VIEWMODELS FOR DATA BINDINGS
// =============================================================================

public partial class CloudSourceVm : ObservableObject
{
    public string ProviderId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BucketOrPath { get; set; } = "";
    public string RegionOrEndpoint { get; set; } = "";
    public string? CustomLocalPath { get; set; }
    public bool IsAdditionalLocal { get; set; }
    public bool IsAllSources => string.Equals(ProviderId, "all", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _status = "Checking"; // Connected, Validated, Error, Disabled

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private int _backupCount;

    [ObservableProperty]
    private long _totalDataBytes;

    [ObservableProperty]
    private string _lastSyncDisplay = "-";

    [ObservableProperty]
    private string _latencyDisplay = "-";

    [ObservableProperty]
    private bool _isTesting;

    public string TotalDataDisplay => FormatBytes(TotalDataBytes);

    public Brush StatusBrush => Status switch
    {
        "Connected" or "Validated" => Application.Current?.TryFindResource("BrushJade") as Brush ?? Brushes.Green,
        "Error" => Application.Current?.TryFindResource("BrushCoral") as Brush ?? Brushes.Red,
        "Testing" => Application.Current?.TryFindResource("BrushCobalt") as Brush ?? Brushes.Blue,
        _ => Application.Current?.TryFindResource("BrushInkMuted") as Brush ?? Brushes.Gray
    };

    public Brush StatusSoftBrush => Status switch
    {
        "Connected" or "Validated" => Application.Current?.TryFindResource("BrushJadeSoft") as Brush ?? Brushes.LightGreen,
        "Error" => Application.Current?.TryFindResource("BrushCoralSoft") as Brush ?? Brushes.Pink,
        "Testing" => Application.Current?.TryFindResource("BrushCobaltSoft") as Brush ?? Brushes.LightBlue,
        _ => Application.Current?.TryFindResource("BrushSunken") as Brush ?? Brushes.LightGray
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public partial class SqlDiscoveredDatabaseVm : ObservableObject
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";

    [ObservableProperty]
    private int _fullCount;

    [ObservableProperty]
    private int _diffCount;

    [ObservableProperty]
    private int _logCount;

    public int TotalBackups => FullCount + DiffCount + LogCount;

    [ObservableProperty]
    private DateTime? _latestBackup;

    [ObservableProperty]
    private DateTime? _earliestBackup;

    [ObservableProperty]
    private long _totalSizeBytes;

    public string TotalSizeDisplay => FormatBytes(TotalSizeBytes);
    public string LatestBackupDisplay => LatestBackup.HasValue ? LatestBackup.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-";
    public string RangeDisplay => (EarliestBackup.HasValue && LatestBackup.HasValue)
        ? $"{EarliestBackup.Value.ToLocalTime():MM/dd HH:mm} - {LatestBackup.Value.ToLocalTime():MM/dd HH:mm}"
        : "-";

    public List<CloudArchiveItem> AllBackups { get; set; } = [];

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class SqlTimelinePointVm
{
    public DateTime Timestamp { get; set; }
    public string BackupType { get; set; } = "FULL";
    public bool IsInRestoreChain { get; set; }
    public string DisplayName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeDisplay => FormatBytes(SizeBytes);
    public CloudArchiveItem ArchiveItem { get; set; } = null!;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class SqlPlannedStepVm
{
    public int StepNumber { get; set; }
    public string FileName { get; set; } = "";
    public string Type { get; set; } = "FULL"; // FULL, DIFF, LOG, STOPAT
    public string Description { get; set; } = "";
    public string SizeDisplay { get; set; } = "-";
    public string TimestampDisplay { get; set; } = "-";

    public Brush TypeBrush => Type switch
    {
        "FULL" => Application.Current?.TryFindResource("BrushCobalt") as Brush ?? Brushes.Blue,
        "DIFF" => Application.Current?.TryFindResource("BrushSky") as Brush ?? Brushes.DeepSkyBlue,
        "LOG" => Application.Current?.TryFindResource("BrushInkMuted") as Brush ?? Brushes.Gray,
        _ => Application.Current?.TryFindResource("BrushJade") as Brush ?? Brushes.Green
    };

    public Brush TypeSoftBrush => Type switch
    {
        "FULL" => Application.Current?.TryFindResource("BrushCobaltSoft") as Brush ?? Brushes.LightBlue,
        "DIFF" => Application.Current?.TryFindResource("BrushSkySoft") as Brush ?? Brushes.LightCyan,
        "LOG" => Application.Current?.TryFindResource("BrushSunken") as Brush ?? Brushes.LightGray,
        _ => Application.Current?.TryFindResource("BrushJadeSoft") as Brush ?? Brushes.LightGreen
    };
}

public partial class ArchiveExplorerItemVm : ObservableObject
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsIisConfig { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? LastModified { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    public string SizeDisplay => IsDirectory ? "-" : FormatBytes(SizeBytes);
    public string DateDisplay => LastModified.HasValue ? LastModified.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class DrillHistoryItemVm
{
    public string Database { get; set; } = "";
    public string ScratchDatabaseName { get; set; } = "";
    public bool Success { get; set; }
    public double RtoSeconds { get; set; }
    public bool CheckDbPassed { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; }
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string RtoDisplay => $"{RtoSeconds:F1}s";
}

// =============================================================================
// MAIN CLOUD RECOVERY VIEWMODEL
// =============================================================================

public partial class RecoveryViewModel : ObservableObject
{
    private readonly Func<AppConfig> _getConfig;
    private readonly ConfigurationService _configService;
    private readonly RecoveryService _recoveryService;
    private readonly SqlRestoreOrchestrator? _sqlOrchestrator;
    private readonly SqlServerRestoreService _sqlRestoreService;
    private readonly SqlServerBackupService _sqlBackupService;
    private readonly RestoreDrillService? _drillService;
    private readonly IEnumerable<IStorageProvider> _storageProviders;
    private readonly SqliteHistoryRepository? _historyRepo;
    private readonly ICredentialProtector _crypto;

    private CancellationTokenSource? _sqlRestoreCts;
    private CancellationTokenSource? _fileRestoreCts;
    private CancellationTokenSource? _drillCts;

    // -------------------------------------------------------------------------
    // NAVIGATION TABS (§2)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboardTab))]
    [NotifyPropertyChangedFor(nameof(IsSqlServerTab))]
    [NotifyPropertyChangedFor(nameof(IsFilesTab))]
    [NotifyPropertyChangedFor(nameof(IsDrillsTab))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab))]
    private string _selectedTab = "Dashboard"; // Dashboard, SqlServer, FilesAndSites, Drills, History

    public bool IsDashboardTab => SelectedTab == "Dashboard";
    public bool IsSqlServerTab => SelectedTab == "SqlServer";
    public bool IsFilesTab => SelectedTab == "FilesAndSites";
    public bool IsDrillsTab => SelectedTab == "Drills";
    public bool IsHistoryTab => SelectedTab == "History";

    // -------------------------------------------------------------------------
    // DASHBOARD & SUMMARY (§1)
    // -------------------------------------------------------------------------
    public ObservableCollection<CloudSourceVm> ConfiguredSources { get; } = [];

    [ObservableProperty]
    private int _totalRecoveryPoints;

    [ObservableProperty]
    private string _latestBackupDisplay = "No backups found";

    [ObservableProperty]
    private string _totalRecoverableDataDisplay = "0 B";

    [ObservableProperty]
    private string _lastVerificationDisplay = "Never tested";

    [ObservableProperty]
    private string _recoveryHealthStatus = "Checking";

    [ObservableProperty]
    private bool _isLoadingDashboard;

    // -------------------------------------------------------------------------
    // SQL POINT-IN-TIME RECOVERY (§3)
    // -------------------------------------------------------------------------
    public ObservableCollection<CloudSourceVm> SqlSources { get; } = [];

    [ObservableProperty]
    private CloudSourceVm? _selectedSqlSource;

    public ObservableCollection<SqlDiscoveredDatabaseVm> DiscoveredDatabases { get; } = [];

    [ObservableProperty]
    private SqlDiscoveredDatabaseVm? _selectedDatabase;

    public ObservableCollection<SqlTimelinePointVm> TimelineBackups { get; } = [];

    [ObservableProperty]
    private DateTime _selectedRecoveryTime = DateTime.UtcNow;

    [ObservableProperty]
    private string _sqlPointInTimeDisplay = "-";

    public ObservableCollection<SqlPlannedStepVm> PlannedSequence { get; } = [];

    [ObservableProperty]
    private bool _hasValidChain;

    [ObservableProperty]
    private string _chainValidationMessage = "";

    [ObservableProperty]
    private string _targetServer = "localhost";

    [ObservableProperty]
    private string _targetDatabaseName = "";

    [ObservableProperty]
    private bool _isRestoreAsNew;

    [ObservableProperty]
    private bool _isOverwriteExisting;

    [ObservableProperty]
    private string _sqlPassphrase = "";

    [ObservableProperty]
    private string _alternateDataPath = "";

    [ObservableProperty]
    private string _alternateLogPath = "";

    [ObservableProperty]
    private bool _isSqlRestoring;

    [ObservableProperty]
    private double _sqlRestoreProgress;

    [ObservableProperty]
    private string _sqlRestorePhase = "";

    [ObservableProperty]
    private string _sqlRestoreMessage = "";

    // -------------------------------------------------------------------------
    // FILES AND SITES RECOVERY (§4)
    // -------------------------------------------------------------------------
    public ObservableCollection<CloudSourceVm> FileSources { get; } = [];

    [ObservableProperty]
    private CloudSourceVm? _selectedFileSource;

    public ObservableCollection<CloudArchiveItem> AvailableArchives { get; } = [];

    [ObservableProperty]
    private CloudArchiveItem? _selectedArchive;

    [ObservableProperty]
    private bool _isInspectingArchive;

    [ObservableProperty]
    private ArchiveInspectionResult? _archiveInspection;

    public ObservableCollection<ArchiveExplorerItemVm> ArchiveExplorerNodes { get; } = [];
    public ObservableCollection<ArchiveExplorerItemVm> FilteredArchiveExplorerNodes { get; } = [];

    [ObservableProperty]
    private string _searchFilter = "";

    partial void OnSearchFilterChanged(string value) => ApplyArchiveExplorerFilter();

    [ObservableProperty]
    private bool _isIisSite;

    [ObservableProperty]
    private string _iisDetectedName = "";

    [ObservableProperty]
    private string _iisDetectedPath = "";

    [ObservableProperty]
    private string _destinationMode = "Safe"; // Safe, Original, Custom

    [ObservableProperty]
    private string _customDestinationPath = @"C:\UniBackup\Restore";

    [ObservableProperty]
    private string _writeMode = "safe"; // safe, overwrite, skip

    [ObservableProperty]
    private bool _isFileRestoring;

    [ObservableProperty]
    private double _fileRestoreProgress;

    [ObservableProperty]
    private string _fileRestorePhase = "";

    [ObservableProperty]
    private string _fileRestoreMessage = "";

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private string _previewContent = "";

    [ObservableProperty]
    private string _previewFileName = "";

    // -------------------------------------------------------------------------
    // RESTORE DRILLS (§5)
    // -------------------------------------------------------------------------
    public ObservableCollection<BackupJob> AvailableDrillJobs { get; } = [];

    [ObservableProperty]
    private BackupJob? _selectedDrillJob;

    [ObservableProperty]
    private string _drillDatabaseName = "";

    [ObservableProperty]
    private string _scratchDatabaseName = "";

    [ObservableProperty]
    private bool _isRunningDrill;

    [ObservableProperty]
    private string _drillProgressMessage = "";

    [ObservableProperty]
    private string _drillLastRtoDisplay = "-";

    [ObservableProperty]
    private string _drillLastOutcomeDisplay = "Ready to test";

    [ObservableProperty]
    private bool _drillLastPassed;

    public ObservableCollection<DrillHistoryItemVm> DrillHistory { get; } = [];

    // -------------------------------------------------------------------------
    // RECOVERY HISTORY (§6)
    // -------------------------------------------------------------------------
    public ObservableCollection<RecoveryOperationRecord> HistoryRecords { get; } = [];
    public ObservableCollection<RecoveryOperationRecord> FilteredHistoryRecords { get; } = [];

    [ObservableProperty]
    private RecoveryOperationRecord? _selectedHistoryRecord;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsHistoryFilterSql))]
    [NotifyPropertyChangedFor(nameof(IsHistoryFilterFiles))]
    [NotifyPropertyChangedFor(nameof(IsHistoryFilterDrills))]
    private string _historyTypeFilter = "All";

    public bool IsHistoryFilterAll => string.Equals(HistoryTypeFilter, "All", StringComparison.OrdinalIgnoreCase);
    public bool IsHistoryFilterSql => string.Equals(HistoryTypeFilter, "SqlPointInTime", StringComparison.OrdinalIgnoreCase);
    public bool IsHistoryFilterFiles => string.Equals(HistoryTypeFilter, "Files", StringComparison.OrdinalIgnoreCase);
    public bool IsHistoryFilterDrills => string.Equals(HistoryTypeFilter, "RestoreDrill", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<string> HistoryStatusFilterOptions { get; } = ["All", "SUCCESS", "FAILED", "CANCELLED"];

    [ObservableProperty]
    private string _historyStatusFilter = "All";

    [ObservableProperty]
    private string _historySearchQuery = "";

    // -------------------------------------------------------------------------
    // CONSTRUCTOR & INITIALIZATION
    // -------------------------------------------------------------------------
    public RecoveryViewModel(
        Func<AppConfig> getConfig,
        ConfigurationService configService,
        RecoveryService recoveryService,
        SqlRestoreOrchestrator? sqlOrchestrator,
        SqlServerRestoreService sqlRestoreService,
        SqlServerBackupService sqlBackupService,
        RestoreDrillService? drillService,
        IEnumerable<IStorageProvider> storageProviders,
        SqliteHistoryRepository? historyRepo,
        ICredentialProtector crypto)
    {
        _getConfig = getConfig;
        _configService = configService;
        _recoveryService = recoveryService;
        _sqlOrchestrator = sqlOrchestrator;
        _sqlRestoreService = sqlRestoreService;
        _sqlBackupService = sqlBackupService;
        _drillService = drillService;
        _storageProviders = storageProviders;
        _historyRepo = historyRepo;
        _crypto = crypto;
    }

    public async Task InitializeAsync()
    {
        await RefreshDashboardAsync();
        await LoadDrillJobsAsync();
        await LoadHistoryAsync();
    }

    // =========================================================================
    // 1. DASHBOARD REFRESH & DISCOVERY (§1)
    // =========================================================================
    [RelayCommand]
    public async Task RefreshDashboardAsync()
    {
        if (IsLoadingDashboard) return;
        IsLoadingDashboard = true;

        try
        {
            var config = _getConfig();
            var prevSqlKey = (SelectedSqlSource?.ProviderId, SelectedSqlSource?.CustomLocalPath);
            var prevFileKey = (SelectedFileSource?.ProviderId, SelectedFileSource?.CustomLocalPath);

            ConfiguredSources.Clear();
            SqlSources.Clear();
            FileSources.Clear();

            // Discover all configured storage providers (including multi-local paths)
            var sources = DiscoverConfiguredSources(config);
            if (sources.Count > 1)
            {
                var allSource = new CloudSourceVm
                {
                    ProviderId = "all",
                    DisplayName = $"All Configured Sources ({sources.Count})",
                    BucketOrPath = "Unified Multi-Provider View",
                    RegionOrEndpoint = "All Active",
                    Status = "Connected"
                };
                SqlSources.Add(allSource);
                FileSources.Add(allSource);
            }

            foreach (var s in sources)
            {
                ConfiguredSources.Add(s);
                SqlSources.Add(s);
                FileSources.Add(s);
            }

            SelectedSqlSource = SqlSources.FirstOrDefault(s =>
                s.ProviderId == prevSqlKey.ProviderId && s.CustomLocalPath == prevSqlKey.CustomLocalPath)
                ?? SqlSources.FirstOrDefault();

            SelectedFileSource = FileSources.FirstOrDefault(s =>
                s.ProviderId == prevFileKey.ProviderId && s.CustomLocalPath == prevFileKey.CustomLocalPath)
                ?? FileSources.FirstOrDefault();

            // Validate and list all configured providers concurrently with per-provider fault isolation
            var checkTasks = ConfiguredSources.Select(async source =>
            {
                try
                {
                    var scopedConfig = CreateScopedConfigForSource(config, source);
                    var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(source.ProviderId, StringComparison.OrdinalIgnoreCase));
                    if (provider == null) return;

                    var sw = Stopwatch.StartNew();
                    var valRes = await provider.ValidateConnectionAsync(scopedConfig);
                    sw.Stop();

                    if (valRes.IsSuccess)
                    {
                        source.Status = "Connected";
                        source.LatencyDisplay = $"{sw.ElapsedMilliseconds} ms";
                        source.StatusMessage = "Connection verified";

                        var listRes = await provider.ListArchivesAsync(scopedConfig);
                        if (listRes.IsSuccess)
                        {
                            source.BackupCount = listRes.Value.Count;
                            source.TotalDataBytes = listRes.Value.Sum(a => a.SizeBytes);

                            var latest = listRes.Value.OrderByDescending(a => a.LastModified).FirstOrDefault();
                            if (latest?.LastModified != null)
                            {
                                source.LastSyncDisplay = latest.LastModified.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                            }
                        }
                    }
                    else
                    {
                        source.Status = "Error";
                        source.StatusMessage = valRes.Error ?? "Connection failed";
                        source.LatencyDisplay = "-";
                    }
                }
                catch (Exception ex)
                {
                    source.Status = "Error";
                    source.StatusMessage = ex.Message;
                    source.LatencyDisplay = "-";
                    Log.Warning(ex, "Provider {Provider} validation failed during dashboard refresh", source.DisplayName);
                }
            }).ToList();

            await Task.WhenAll(checkTasks);

            int totalPoints = ConfiguredSources.Sum(s => s.BackupCount);
            long totalBytes = ConfiguredSources.Sum(s => s.TotalDataBytes);

            TotalRecoveryPoints = totalPoints;
            TotalRecoverableDataDisplay = FormatBytes(totalBytes);
            var latestSync = ConfiguredSources
                .Where(s => s.LastSyncDisplay != "-")
                .Select(s => s.LastSyncDisplay)
                .OrderByDescending(s => s)
                .FirstOrDefault();
            LatestBackupDisplay = latestSync ?? "No backups found";

            RecoveryHealthStatus = ConfiguredSources.Any(s => s.Status == "Error") ? "Attention Needed"
                : (totalPoints > 0 ? "Healthy" : "No Backups");

            // Auto-discover databases for SQL recovery if on SQL tab
            if (SelectedSqlSource != null)
            {
                await DiscoverDatabasesForSourceAsync(SelectedSqlSource);
            }

            // Auto-load archives for Files tab
            if (SelectedFileSource != null)
            {
                await LoadArchivesForFileSourceAsync(SelectedFileSource);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh cloud recovery dashboard: {Message}", ex.Message);
        }
        finally
        {
            IsLoadingDashboard = false;
        }
    }

    private List<CloudSourceVm> DiscoverConfiguredSources(AppConfig config) => GetConfiguredSources(config);

    public static List<CloudSourceVm> GetConfiguredSources(AppConfig config)
    {
        var list = new List<CloudSourceVm>();

        if (config.Aws.Enabled && !string.IsNullOrWhiteSpace(config.Aws.BucketName))
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "s3",
                DisplayName = string.Equals(config.Aws.Preset, "cloudflare_r2", StringComparison.OrdinalIgnoreCase) ? "Cloudflare R2"
                    : string.Equals(config.Aws.Preset, "wasabi", StringComparison.OrdinalIgnoreCase) ? "Wasabi Hot Cloud"
                    : string.Equals(config.Aws.Preset, "backblaze_b2", StringComparison.OrdinalIgnoreCase) ? "Backblaze B2"
                    : "Amazon S3",
                BucketOrPath = config.Aws.BucketName,
                RegionOrEndpoint = config.Aws.Region
            });
        }

        if (config.Azure.Enabled && !string.IsNullOrWhiteSpace(config.Azure.ContainerName))
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "azure",
                DisplayName = "Azure Blob Storage",
                BucketOrPath = config.Azure.ContainerName,
                RegionOrEndpoint = "Azure Cloud"
            });
        }

        if (config.GDrive.Enabled)
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "gdrive",
                DisplayName = "Google Drive",
                BucketOrPath = string.IsNullOrWhiteSpace(config.GDrive.TargetFolder) ? "/Backups" : config.GDrive.TargetFolder,
                RegionOrEndpoint = "Google Cloud"
            });
        }

        if (config.Local.Enabled && !string.IsNullOrWhiteSpace(config.Local.TargetPath))
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "local",
                DisplayName = "Local / Network Repository",
                BucketOrPath = config.Local.TargetPath,
                RegionOrEndpoint = Environment.MachineName,
                CustomLocalPath = config.Local.TargetPath
            });
        }

        if (config.Local.AdditionalPaths != null)
        {
            foreach (var extraPath in config.Local.AdditionalPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (config.Local.Enabled && string.Equals(extraPath, config.Local.TargetPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var folderName = Path.GetFileName(extraPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                list.Add(new CloudSourceVm
                {
                    ProviderId = "local",
                    DisplayName = $"Local Folder ({(string.IsNullOrEmpty(folderName) ? extraPath : folderName)})",
                    BucketOrPath = extraPath,
                    RegionOrEndpoint = Environment.MachineName,
                    CustomLocalPath = extraPath,
                    IsAdditionalLocal = true
                });
            }
        }

        if (config.Ftp.Enabled && !string.IsNullOrWhiteSpace(config.Ftp.Host))
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "ftp",
                DisplayName = $"FTP Server ({config.Ftp.Host})",
                BucketOrPath = config.Ftp.RemoteFolder,
                RegionOrEndpoint = $"{config.Ftp.Host}:{config.Ftp.Port}"
            });
        }

        if (config.Sftp.Enabled && !string.IsNullOrWhiteSpace(config.Sftp.Host))
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "sftp",
                DisplayName = $"SFTP Server ({config.Sftp.Host})",
                BucketOrPath = config.Sftp.RemoteFolder,
                RegionOrEndpoint = $"{config.Sftp.Host}:{config.Sftp.Port}"
            });
        }

        if (config.WebDav.Enabled && !string.IsNullOrWhiteSpace(config.WebDav.ServerUrl))
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "webdav",
                DisplayName = "WebDAV Repository",
                BucketOrPath = config.WebDav.RemoteFolder,
                RegionOrEndpoint = config.WebDav.ServerUrl
            });
        }

        if (config.Dropbox.Enabled)
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "dropbox",
                DisplayName = "Dropbox",
                BucketOrPath = config.Dropbox.RemoteFolder,
                RegionOrEndpoint = "Dropbox Cloud"
            });
        }

        if (config.OneDrive.Enabled)
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "onedrive",
                DisplayName = "Microsoft OneDrive",
                BucketOrPath = config.OneDrive.RemoteFolder,
                RegionOrEndpoint = "Microsoft Graph"
            });
        }

        if (config.Box.Enabled)
        {
            list.Add(new CloudSourceVm
            {
                ProviderId = "box",
                DisplayName = "Box Storage",
                BucketOrPath = config.Box.FolderId,
                RegionOrEndpoint = "Box Enterprise"
            });
        }

        return list;
    }

    public static AppConfig CreateScopedConfigForSource(AppConfig baseConfig, CloudSourceVm? source)
    {
        if (source == null || string.IsNullOrWhiteSpace(source.CustomLocalPath))
            return baseConfig;

        return new AppConfig
        {
            Backup = baseConfig.Backup,
            Email = baseConfig.Email,
            Schedule = baseConfig.Schedule,
            Webhooks = baseConfig.Webhooks,
            Aws = baseConfig.Aws,
            Azure = baseConfig.Azure,
            GDrive = baseConfig.GDrive,
            Local = new LocalStorageSettings
            {
                Enabled = true,
                TargetPath = source.CustomLocalPath,
                AdditionalPaths = baseConfig.Local.AdditionalPaths,
                RetentionDays = baseConfig.Local.RetentionDays
            },
            WebDav = baseConfig.WebDav,
            Ftp = baseConfig.Ftp,
            Sftp = baseConfig.Sftp,
            Dropbox = baseConfig.Dropbox,
            OneDrive = baseConfig.OneDrive,
            Box = baseConfig.Box,
            Security = baseConfig.Security,
            NotificationMatrix = baseConfig.NotificationMatrix,
            Heartbeat = baseConfig.Heartbeat,
            Rpo = baseConfig.Rpo,
            Prometheus = baseConfig.Prometheus
        };
    }

    private AppConfig ResolveConfigForArchive(AppConfig baseConfig, CloudArchiveItem? archive, CloudSourceVm? fallbackSource)
    {
        if (fallbackSource != null && !fallbackSource.IsAllSources)
            return CreateScopedConfigForSource(baseConfig, fallbackSource);

        if (archive != null && !string.IsNullOrWhiteSpace(archive.Destination))
        {
            var matched = ConfiguredSources.FirstOrDefault(s =>
                s.ProviderId.Equals(archive.Destination, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(s.CustomLocalPath) || archive.KeyOrId.StartsWith(s.CustomLocalPath, StringComparison.OrdinalIgnoreCase)));
            if (matched != null)
                return CreateScopedConfigForSource(baseConfig, matched);
        }

        return baseConfig;
    }

    [RelayCommand]
    public async Task AddLocalRecoveryPathAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Local or Network Backup Repository Folder"
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var config = _getConfig();
        if (!config.Local.AdditionalPaths.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase) &&
            !(config.Local.Enabled && string.Equals(config.Local.TargetPath, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            config.Local.AdditionalPaths.Add(dialog.FolderName);
            await _configService.SaveConfigAsync(config);
            await RefreshDashboardAsync();
            NotificationHub.ShowSuccess($"Added local recovery repository: {dialog.FolderName}", "Repository Added");
        }
    }

    [RelayCommand]
    public async Task RemoveLocalRecoveryPathAsync(CloudSourceVm? source)
    {
        if (source == null || !source.IsAdditionalLocal || string.IsNullOrWhiteSpace(source.CustomLocalPath))
            return;

        var config = _getConfig();
        config.Local.AdditionalPaths.RemoveAll(p => string.Equals(p, source.CustomLocalPath, StringComparison.OrdinalIgnoreCase));
        await _configService.SaveConfigAsync(config);
        await RefreshDashboardAsync();
        NotificationHub.ShowInfo($"Removed local recovery repository: {source.CustomLocalPath}", "Repository Removed");
    }

    [RelayCommand]
    public async Task ValidateSourceAsync(CloudSourceVm? source)
    {
        if (source == null) return;
        source.IsTesting = true;
        source.Status = "Testing";

        try
        {
            var config = CreateScopedConfigForSource(_getConfig(), source);
            var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(source.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider == null) return;

            var sw = Stopwatch.StartNew();
            var res = await provider.ValidateConnectionAsync(config);
            sw.Stop();

            if (res.IsSuccess)
            {
                source.Status = "Connected";
                source.LatencyDisplay = $"{sw.ElapsedMilliseconds} ms";
                source.StatusMessage = "Connection verified successfully.";
                NotificationHub.ShowSuccess($"Connection to {source.DisplayName} verified ({sw.ElapsedMilliseconds} ms).", "Validation Successful");
            }
            else
            {
                source.Status = "Error";
                source.StatusMessage = res.Error ?? "Validation failed";
                NotificationHub.ShowError($"Connection to {source.DisplayName} failed: {res.Error}", "Validation Failed");
            }
        }
        finally
        {
            source.IsTesting = false;
        }
    }

    [RelayCommand]
    public void BrowseSource(CloudSourceVm? source)
    {
        if (source == null) return;
        bool alreadySelected = ReferenceEquals(SelectedFileSource, source);
        SelectedFileSource = source;
        SelectedTab = "FilesAndSites";
        if (alreadySelected)
        {
            _ = LoadArchivesForFileSourceAsync(source);
        }
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        SelectedTab = tab;
    }

    // =========================================================================
    // 2. SQL POINT-IN-TIME RECOVERY (§3)
    // =========================================================================
    partial void OnSelectedSqlSourceChanged(CloudSourceVm? value)
    {
        if (value != null)
        {
            _ = DiscoverDatabasesForSourceAsync(value);
        }
    }

    public async Task DiscoverDatabasesForSourceAsync(CloudSourceVm source)
    {
        try
        {
            var config = _getConfig();
            var allArchives = new List<CloudArchiveItem>();

            var targetSources = source.IsAllSources
                ? ConfiguredSources.ToList()
                : [source];

            var tasks = targetSources.Select(async s =>
            {
                try
                {
                    var scopedConfig = CreateScopedConfigForSource(config, s);
                    var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(s.ProviderId, StringComparison.OrdinalIgnoreCase));
                    if (provider == null) return new List<CloudArchiveItem>();

                    var listRes = await provider.ListArchivesAsync(scopedConfig);
                    return listRes.IsSuccess ? listRes.Value : new List<CloudArchiveItem>();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "SQL database discovery failed for {Source}", s.DisplayName);
                    return new List<CloudArchiveItem>();
                }
            });

            var results = await Task.WhenAll(tasks);
            foreach (var items in results)
            {
                allArchives.AddRange(items);
            }

            var grouped = GroupSqlBackupsByDatabase(allArchives);

            DiscoveredDatabases.Clear();
            foreach (var db in grouped)
            {
                DiscoveredDatabases.Add(db);
            }

            SelectedDatabase = DiscoveredDatabases.FirstOrDefault();
            if (SelectedDatabase == null)
            {
                PlannedSequence.Clear();
                TimelineBackups.Clear();
                HasValidChain = false;
                ChainValidationMessage = "No SQL Server backups found in the selected source.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to discover SQL databases: {Message}", ex.Message);
        }
    }

    public static List<SqlDiscoveredDatabaseVm> GroupSqlBackupsByDatabase(IEnumerable<CloudArchiveItem> archives)
    {
        var dbs = new Dictionary<string, SqlDiscoveredDatabaseVm>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in archives)
        {
            var fileName = item.FileName;
            if (SqlBackupFileName.TryParse(fileName, out var parsed))
            {
                if (!dbs.TryGetValue(parsed.Database, out var dbVm))
                {
                    dbVm = new SqlDiscoveredDatabaseVm
                    {
                        Name = parsed.Database,
                        Server = string.IsNullOrEmpty(item.ServerIpOrTag) ? Environment.MachineName : item.ServerIpOrTag
                    };
                    dbs[parsed.Database] = dbVm;
                }

                if (parsed.Type == SqlBackupType.Full) dbVm.FullCount++;
                else if (parsed.Type == SqlBackupType.Diff) dbVm.DiffCount++;
                else dbVm.LogCount++;

                dbVm.TotalSizeBytes += item.SizeBytes;
                dbVm.AllBackups.Add(item);

                if (dbVm.LatestBackup == null || parsed.Timestamp > dbVm.LatestBackup)
                {
                    dbVm.LatestBackup = parsed.Timestamp;
                }
                if (dbVm.EarliestBackup == null || parsed.Timestamp < dbVm.EarliestBackup)
                {
                    dbVm.EarliestBackup = parsed.Timestamp;
                }
            }
        }

        return dbs.Values.OrderBy(d => d.Name).ToList();
    }

    partial void OnSelectedDatabaseChanged(SqlDiscoveredDatabaseVm? value)
    {
        if (value == null)
        {
            TimelineBackups.Clear();
            PlannedSequence.Clear();
            TargetDatabaseName = "";
            return;
        }

        TargetDatabaseName = value.Name;

        // Build Timeline points
        TimelineBackups.Clear();
        foreach (var b in value.AllBackups)
        {
            if (SqlBackupFileName.TryParse(b.DisplayName, out var parsed))
            {
                TimelineBackups.Add(new SqlTimelinePointVm
                {
                    Timestamp = parsed.Timestamp,
                    BackupType = parsed.Type.ToString().ToUpperInvariant(),
                    DisplayName = b.DisplayName,
                    SizeBytes = b.SizeBytes,
                    ArchiveItem = b
                });
            }
        }

        // Default recovery time to latest backup
        if (value.LatestBackup.HasValue)
        {
            SelectedRecoveryTime = value.LatestBackup.Value;
        }

        RecalculateRestoreChain();
    }

    partial void OnSelectedRecoveryTimeChanged(DateTime value)
    {
        SqlPointInTimeDisplay = $"{value:yyyy-MM-dd HH:mm:ss} UTC";
        RecalculateRestoreChain();
    }

    [RelayCommand]
    public void SetLatestRecoveryPoint()
    {
        if (SelectedDatabase?.LatestBackup != null)
        {
            SelectedRecoveryTime = SelectedDatabase.LatestBackup.Value;
            NotificationHub.ShowInfo("Point in time set to newest available backup.", "Recovery Point Updated");
        }
    }

    public void RecalculateRestoreChain()
    {
        if (SelectedDatabase == null || SelectedDatabase.AllBackups.Count == 0)
        {
            PlannedSequence.Clear();
            HasValidChain = false;
            ChainValidationMessage = "No database selected.";
            return;
        }

        var planRes = SqlRestorePlanner.Plan(SelectedDatabase.AllBackups, SelectedDatabase.Name, SelectedRecoveryTime);
        if (planRes.IsFailure)
        {
            PlannedSequence.Clear();
            HasValidChain = false;
            ChainValidationMessage = planRes.Error ?? "Could not build valid restore chain.";
            return;
        }

        var plan = planRes.Value!;
        PlannedSequence.Clear();
        int step = 1;

        // Base full
        PlannedSequence.Add(new SqlPlannedStepVm
        {
            StepNumber = step++,
            FileName = plan.FullBackup.DisplayName,
            Type = "FULL",
            Description = "Base full backup (RESTORE WITH NORECOVERY)",
            SizeDisplay = plan.FullBackup.SizeDisplay,
            TimestampDisplay = plan.FullBackup.UploadedTimeDisplay
        });

        // Differential (if present)
        if (plan.DiffBackup != null)
        {
            PlannedSequence.Add(new SqlPlannedStepVm
            {
                StepNumber = step++,
                FileName = plan.DiffBackup.DisplayName,
                Type = "DIFF",
                Description = "Differential base rollup (RESTORE WITH NORECOVERY)",
                SizeDisplay = plan.DiffBackup.SizeDisplay,
                TimestampDisplay = plan.DiffBackup.UploadedTimeDisplay
            });
        }

        // Sequential logs
        for (int i = 0; i < plan.LogBackups.Count; i++)
        {
            var log = plan.LogBackups[i];
            bool isLast = (i == plan.LogBackups.Count - 1);
            PlannedSequence.Add(new SqlPlannedStepVm
            {
                StepNumber = step++,
                FileName = log.DisplayName,
                Type = "LOG",
                Description = isLast
                    ? $"Final transaction log (RESTORE WITH RECOVERY, STOPAT '{SelectedRecoveryTime:yyyy-MM-dd HH:mm:ss}')"
                    : "Transaction log stream (RESTORE WITH NORECOVERY)",
                SizeDisplay = log.SizeDisplay,
                TimestampDisplay = log.UploadedTimeDisplay
            });
        }

        // Final STOPAT bring-online summary step
        PlannedSequence.Add(new SqlPlannedStepVm
        {
            StepNumber = step++,
            FileName = $"Target: {SelectedRecoveryTime:yyyy-MM-dd HH:mm:ss}",
            Type = "STOPAT",
            Description = "Bring database online and rollback uncommitted transactions (RECOVERY)",
            SizeDisplay = "-",
            TimestampDisplay = $"{SelectedRecoveryTime:yyyy-MM-dd HH:mm:ss}"
        });

        HasValidChain = true;
        ChainValidationMessage = $"Complete chain verified: 1 FULL{(plan.DiffBackup != null ? " + 1 DIFF" : "")} + {plan.LogBackups.Count} LOGs.";

        // Update Timeline mark flags
        var chainKeys = new HashSet<string>(plan.Sequence.Select(s => s.KeyOrId));
        foreach (var p in TimelineBackups)
        {
            p.IsInRestoreChain = chainKeys.Contains(p.ArchiveItem.KeyOrId);
        }
    }

    [RelayCommand]
    public async Task ExecuteSqlRestoreAsync()
    {
        if (IsSqlRestoring || SelectedDatabase == null || !HasValidChain) return;

        var targetDb = string.IsNullOrWhiteSpace(TargetDatabaseName) ? SelectedDatabase.Name : TargetDatabaseName;

        // Confirmation dialog
        var overwriteMsg = IsOverwriteExisting
            ? "\n\nWARNING: Overwrite existing database is checked! Existing data will be overwritten."
            : "";
        var confirmResult = MessageBox.Show(
            $"Restore SQL Server database '{SelectedDatabase.Name}' as '{targetDb}'?\n\nTarget SQL Server: {TargetServer}\nTarget Second: {SelectedRecoveryTime:yyyy-MM-dd HH:mm:ss} UTC{overwriteMsg}",
            "Confirm Database Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes) return;

        IsSqlRestoring = true;
        SqlRestoreProgress = 5;
        SqlRestorePhase = "Pre-flight validation";
        SqlRestoreMessage = "Validating target SQL Server and backup chain...";

        _sqlRestoreCts = new CancellationTokenSource();
        var ct = _sqlRestoreCts.Token;

        var opRecord = new RecoveryOperationRecord
        {
            Provider = SelectedSqlSource?.ProviderId ?? "s3",
            SourceBackup = SelectedDatabase.Name,
            TargetName = targetDb,
            RecoveryType = "SqlPointInTime",
            StartTime = DateTime.UtcNow,
            Status = "RUNNING"
        };
        if (_historyRepo != null) await _historyRepo.RecordRecoveryOperationAsync(opRecord);

        var sw = Stopwatch.StartNew();

        try
        {
            var config = ResolveConfigForArchive(_getConfig(), SelectedDatabase.AllBackups.FirstOrDefault(), SelectedSqlSource);

            // Match backup job for credentials
            var jobs = await _configService.LoadJobsAsync();
            var matchedJob = jobs.FirstOrDefault(j => j.Kind == "sqlserver" && (j.Sql?.Databases.Contains(SelectedDatabase.Name) == true))
                             ?? new BackupJob
                             {
                                 Id = "ad_hoc_restore",
                                 Name = "Ad-Hoc SQL Restore",
                                 Kind = "sqlserver",
                                 Destinations = [SelectedSqlSource?.ProviderId ?? "s3"],
                                 Sql = new SqlServerSource
                                 {
                                     Server = TargetServer,
                                     UseWindowsAuth = true
                                 }
                             };

            if (_sqlOrchestrator == null)
            {
                throw new InvalidOperationException("SQL Restore Orchestrator service is not initialized.");
            }

            var report = new Action<string, double>((msg, pct) =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    SqlRestoreMessage = msg;
                    SqlRestoreProgress = Math.Clamp(pct, 5.0, 99.0);
                    SqlRestorePhase = pct < 20 ? "Pre-flight" : (pct < 80 ? "Downloading" : "Restoring SQL Server");
                });
            });

            var res = await _sqlOrchestrator.RestoreFromCloudAsync(
                config,
                matchedJob,
                SelectedDatabase.Name,
                SelectedRecoveryTime,
                targetDb,
                SelectedSqlSource?.ProviderId,
                string.IsNullOrWhiteSpace(SqlPassphrase) ? null : SqlPassphrase,
                IsOverwriteExisting,
                report,
                ct);

            sw.Stop();

            if (res.IsSuccess)
            {
                SqlRestoreProgress = 100;
                SqlRestorePhase = "Completed";
                SqlRestoreMessage = $"Database '{targetDb}' successfully restored in {sw.Elapsed.TotalSeconds:F1}s!";
                NotificationHub.ShowSuccess($"Database '{targetDb}' restored successfully!", "Restore Finished");

                if (_historyRepo != null)
                {
                    await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "SUCCESS", sw.Elapsed.TotalSeconds, null, SqlRestoreMessage);
                }
            }
            else
            {
                SqlRestorePhase = "Failed";
                SqlRestoreMessage = res.Error ?? "Restore failed.";
                NotificationHub.ShowError($"Database restore failed: {res.Error}", "Restore Failed");

                if (_historyRepo != null)
                {
                    await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "FAILED", sw.Elapsed.TotalSeconds, res.Error, SqlRestoreMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            SqlRestorePhase = "Cancelled";
            SqlRestoreMessage = "Restore was cancelled by the user.";
            if (_historyRepo != null)
            {
                await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "CANCELLED", sw.Elapsed.TotalSeconds, "Cancelled by user", SqlRestoreMessage);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            SqlRestorePhase = "Failed";
            SqlRestoreMessage = ex.Message;
            NotificationHub.ShowError($"Database restore failed: {ex.Message}", "Restore Error");
            if (_historyRepo != null)
            {
                await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "FAILED", sw.Elapsed.TotalSeconds, ex.Message, ex.ToString());
            }
        }
        finally
        {
            IsSqlRestoring = false;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    public void CancelSqlRestore()
    {
        _sqlRestoreCts?.Cancel();
    }

    // =========================================================================
    // 3. FILES AND IIS SITES RECOVERY (§4)
    // =========================================================================
    partial void OnSelectedFileSourceChanged(CloudSourceVm? value)
    {
        if (value != null)
        {
            _ = LoadArchivesForFileSourceAsync(value);
        }
    }

    public async Task LoadArchivesForFileSourceAsync(CloudSourceVm source)
    {
        try
        {
            var config = _getConfig();
            var targetSources = source.IsAllSources
                ? ConfiguredSources.ToList()
                : [source];

            var tasks = targetSources.Select(async s =>
            {
                try
                {
                    var scopedConfig = CreateScopedConfigForSource(config, s);
                    var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(s.ProviderId, StringComparison.OrdinalIgnoreCase));
                    if (provider == null) return new List<CloudArchiveItem>();

                    var listRes = await provider.ListArchivesAsync(scopedConfig);
                    return listRes.IsSuccess ? listRes.Value : new List<CloudArchiveItem>();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Archive listing failed for {Source}", s.DisplayName);
                    return new List<CloudArchiveItem>();
                }
            });

            var results = await Task.WhenAll(tasks);
            var combined = results
                .SelectMany(x => x)
                .Where(a => a.DisplayName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            a.DisplayName.Contains(".zip.ubenc", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.LastModified ?? DateTime.MinValue)
                .ToList();

            AvailableArchives.Clear();
            foreach (var a in combined)
            {
                AvailableArchives.Add(a);
            }

            SelectedArchive = AvailableArchives.FirstOrDefault();
            if (SelectedArchive == null)
            {
                ArchiveExplorerNodes.Clear();
                FilteredArchiveExplorerNodes.Clear();
                ArchiveInspection = null;
                IsIisSite = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load archives for source {Source}: {Message}", source.DisplayName, ex.Message);
        }
    }

    partial void OnSelectedArchiveChanged(CloudArchiveItem? value)
    {
        if (value != null)
        {
            _ = InspectArchiveAsync(value);
        }
        else
        {
            ArchiveExplorerNodes.Clear();
            ArchiveInspection = null;
            IsIisSite = false;
        }
    }

    public async Task InspectArchiveAsync(CloudArchiveItem archive)
    {
        if (IsInspectingArchive) return;
        IsInspectingArchive = true;

        try
        {
            var config = ResolveConfigForArchive(_getConfig(), archive, SelectedFileSource);
            var progress = new Progress<(string phase, double percent, string message)>(p =>
            {
                FileRestoreMessage = p.message;
            });

            var res = await _recoveryService.InspectArchiveAsync(config, archive, progress);
            if (res.IsSuccess)
            {
                ArchiveInspection = res.Value!;
                IsIisSite = ArchiveInspection.IsIisWebsite;
                IisDetectedName = ArchiveInspection.DetectedSiteName ?? Path.GetFileNameWithoutExtension(archive.DisplayName);
                IisDetectedPath = IsIisSite ? $@"C:\inetpub\wwwroot\{IisDetectedName}" : CustomDestinationPath;

                ArchiveExplorerNodes.Clear();
                foreach (var entry in ArchiveInspection.Entries)
                {
                    ArchiveExplorerNodes.Add(new ArchiveExplorerItemVm
                    {
                        Name = entry.Name,
                        RelativePath = entry.RelativePath,
                        DirectoryPath = entry.DirectoryPath,
                        IsDirectory = entry.IsDirectory,
                        IsIisConfig = entry.IsIisConfig,
                        SizeBytes = entry.SizeBytes,
                        LastModified = entry.LastModified,
                        IsSelected = true // default to all selected
                    });
                }
                ApplyArchiveExplorerFilter();
            }
            else
            {
                NotificationHub.ShowWarning($"Could not inspect archive: {res.Error}", "Archive Inspection");
            }
        }
        finally
        {
            IsInspectingArchive = false;
        }
    }

    public void ApplyArchiveExplorerFilter()
    {
        FilteredArchiveExplorerNodes.Clear();
        var q = SearchFilter?.Trim();
        foreach (var node in ArchiveExplorerNodes)
        {
            if (string.IsNullOrEmpty(q) ||
                node.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                node.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredArchiveExplorerNodes.Add(node);
            }
        }
    }

    [RelayCommand]
    public void SelectAllArchiveFiles()
    {
        foreach (var node in ArchiveExplorerNodes)
        {
            node.IsSelected = true;
        }
    }

    [RelayCommand]
    public void ClearSelectedArchiveFiles()
    {
        foreach (var node in ArchiveExplorerNodes)
        {
            node.IsSelected = false;
        }
    }

    [RelayCommand]
    public void BrowseRestoreDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Destination Directory for Extracted Files"
        };

        if (dialog.ShowDialog() == true)
        {
            CustomDestinationPath = dialog.FolderName;
            DestinationMode = "Custom";
        }
    }

    [RelayCommand]
    public async Task ExecuteFileRestoreAsync()
    {
        if (IsFileRestoring || SelectedArchive == null) return;

        var selectedNodes = ArchiveExplorerNodes.Where(n => n.IsSelected).ToList();
        if (selectedNodes.Count == 0)
        {
            NotificationHub.ShowWarning("Please select at least 1 file or folder to restore.", "No Selection");
            return;
        }

        var targetDir = DestinationMode switch
        {
            "Original" => !string.IsNullOrWhiteSpace(IisDetectedPath) ? IisDetectedPath : CustomDestinationPath,
            _ => CustomDestinationPath
        };

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            NotificationHub.ShowWarning("Please specify a valid destination folder.", "Missing Destination");
            return;
        }

        // Space check
        try
        {
            var root = Path.GetPathRoot(targetDir);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                var drive = new DriveInfo(root);
                long required = selectedNodes.Sum(n => n.SizeBytes);
                if (drive.AvailableFreeSpace < required)
                {
                    NotificationHub.ShowError($"Insufficient disk space on {root}. Available: {FormatBytes(drive.AvailableFreeSpace)}, Required: {FormatBytes(required)}", "Disk Space Error");
                    return;
                }
            }
        }
        catch { }

        IsFileRestoring = true;
        FileRestoreProgress = 10;
        FileRestorePhase = "Downloading / Preparing";
        FileRestoreMessage = $"Preparing to extract {selectedNodes.Count} files...";

        _fileRestoreCts = new CancellationTokenSource();
        var ct = _fileRestoreCts.Token;

        var opRecord = new RecoveryOperationRecord
        {
            Provider = SelectedFileSource?.ProviderId ?? "s3",
            SourceBackup = SelectedArchive.DisplayName,
            TargetName = targetDir,
            RecoveryType = IsIisSite ? "IisWebsite" : "Files",
            StartTime = DateTime.UtcNow,
            Status = "RUNNING"
        };
        if (_historyRepo != null) await _historyRepo.RecordRecoveryOperationAsync(opRecord);

        var sw = Stopwatch.StartNew();

        try
        {
            var config = ResolveConfigForArchive(_getConfig(), SelectedArchive, SelectedFileSource);
            var progress = new Progress<(string phase, double percent, string message)>(p =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    FileRestorePhase = p.phase;
                    FileRestoreProgress = p.percent;
                    FileRestoreMessage = p.message;
                });
            });

            var selectedPaths = selectedNodes.Select(n => n.RelativePath).ToList();
            var res = await _recoveryService.RestoreSelectiveAsync(config, SelectedArchive, selectedPaths, targetDir, WriteMode, progress, ct);

            sw.Stop();

            if (res.IsSuccess)
            {
                FileRestoreProgress = 100;
                FileRestorePhase = "Completed";
                FileRestoreMessage = $"Successfully extracted {res.Value!.FilesRestored} files ({FormatBytes(res.Value.BytesRestored)}) to {res.Value.TargetDirectory}!";
                NotificationHub.ShowSuccess($"Restore completed: {res.Value.FilesRestored} files extracted.", "Restore Succeeded");

                if (_historyRepo != null)
                {
                    await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "SUCCESS", sw.Elapsed.TotalSeconds, null, FileRestoreMessage, res.Value.FilesRestored, res.Value.BytesRestored);
                }
            }
            else
            {
                FileRestorePhase = "Failed";
                FileRestoreMessage = res.Error ?? "File restore failed.";
                NotificationHub.ShowError($"File restore failed: {res.Error}", "Restore Failed");

                if (_historyRepo != null)
                {
                    await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "FAILED", sw.Elapsed.TotalSeconds, res.Error, FileRestoreMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            FileRestorePhase = "Cancelled";
            FileRestoreMessage = "Restore was cancelled by user.";
            if (_historyRepo != null)
            {
                await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "CANCELLED", sw.Elapsed.TotalSeconds, "Cancelled", FileRestoreMessage);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            FileRestorePhase = "Failed";
            FileRestoreMessage = ex.Message;
            NotificationHub.ShowError($"File restore error: {ex.Message}", "Restore Error");
            if (_historyRepo != null)
            {
                await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "FAILED", sw.Elapsed.TotalSeconds, ex.Message, ex.ToString());
            }
        }
        finally
        {
            IsFileRestoring = false;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    public void CancelFileRestore()
    {
        _fileRestoreCts?.Cancel();
    }

    [RelayCommand]
    public async Task PreviewSelectedFileAsync(ArchiveExplorerItemVm? item)
    {
        if (item == null || item.IsDirectory || ArchiveInspection == null) return;

        var ext = Path.GetExtension(item.Name).ToLowerInvariant();
        var textExtensions = new HashSet<string> { ".txt", ".json", ".xml", ".config", ".htm", ".html", ".css", ".js", ".log", ".ini", ".yaml", ".yml", ".md" };

        if (!textExtensions.Contains(ext))
        {
            NotificationHub.ShowInfo($"File preview is only supported for text, JSON, XML, and configuration files ({ext} is binary).", "Preview Unsupported");
            return;
        }

        var res = await _recoveryService.ReadEntryPreviewAsync(ArchiveInspection.LocalZipPath, item.RelativePath);
        if (res.IsSuccess)
        {
            PreviewFileName = item.Name;
            PreviewContent = res.Value!;
            IsPreviewOpen = true;
        }
        else
        {
            NotificationHub.ShowWarning(res.Error ?? "Could not preview file.", "Preview Error");
        }
    }

    [RelayCommand]
    public void ClosePreview()
    {
        IsPreviewOpen = false;
        PreviewContent = "";
    }

    // =========================================================================
    // 4. RESTORE DRILLS (§5)
    // =========================================================================
    public async Task LoadDrillJobsAsync()
    {
        try
        {
            var jobs = await _configService.LoadJobsAsync();
            AvailableDrillJobs.Clear();
            foreach (var j in jobs.Where(j => j.Kind == "sqlserver" && j.Sql != null))
            {
                AvailableDrillJobs.Add(j);
            }

            if (SelectedDrillJob == null && AvailableDrillJobs.Count > 0)
            {
                SelectedDrillJob = AvailableDrillJobs[0];
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load drill jobs: {Message}", ex.Message);
        }
    }

    partial void OnSelectedDrillJobChanged(BackupJob? value)
    {
        if (value?.Sql?.Databases.Count > 0)
        {
            DrillDatabaseName = value.Sql.Databases[0];
            ScratchDatabaseName = $"ub_drill_{FormattingUtils.SanitizePathSegment(DrillDatabaseName)}";
        }
        else
        {
            DrillDatabaseName = "";
            ScratchDatabaseName = "";
        }
    }

    [RelayCommand]
    public async Task ExecuteRestoreDrillAsync()
    {
        if (IsRunningDrill || SelectedDrillJob == null || string.IsNullOrWhiteSpace(DrillDatabaseName)) return;

        IsRunningDrill = true;
        DrillProgressMessage = $"Running automated drill against '{DrillDatabaseName}' -> '{ScratchDatabaseName}'...";

        _drillCts = new CancellationTokenSource();
        var ct = _drillCts.Token;

        var opRecord = new RecoveryOperationRecord
        {
            Provider = SelectedDrillJob.Destinations.FirstOrDefault() ?? "s3",
            SourceBackup = DrillDatabaseName,
            TargetName = ScratchDatabaseName,
            RecoveryType = "RestoreDrill",
            StartTime = DateTime.UtcNow,
            Status = "RUNNING"
        };
        if (_historyRepo != null) await _historyRepo.RecordRecoveryOperationAsync(opRecord);

        var sw = Stopwatch.StartNew();

        try
        {
            var config = _getConfig();
            if (_drillService == null)
            {
                throw new InvalidOperationException("Restore Drill Service is not initialized.");
            }

            var drillResult = await _drillService.RunDrillAsync(config, SelectedDrillJob, DrillDatabaseName, ct);
            sw.Stop();

            DrillLastRtoDisplay = $"{drillResult.RtoSeconds:F1}s";
            DrillLastPassed = drillResult.CheckDbPassed;
            DrillLastOutcomeDisplay = drillResult.CheckDbPassed
                ? "PASSED: DBCC CHECKDB verified 100% consistent"
                : $"FAILED: {drillResult.ErrorMessage}";

            DrillHistory.Insert(0, new DrillHistoryItemVm
            {
                Database = DrillDatabaseName,
                ScratchDatabaseName = ScratchDatabaseName,
                Success = drillResult.Success,
                RtoSeconds = drillResult.RtoSeconds,
                CheckDbPassed = drillResult.CheckDbPassed,
                ErrorMessage = drillResult.ErrorMessage,
                Timestamp = DateTime.UtcNow
            });

            LastVerificationDisplay = drillResult.CheckDbPassed
                ? $"Passed ({drillResult.RtoSeconds:F1}s RTO)"
                : "Failed";

            if (drillResult.CheckDbPassed)
            {
                NotificationHub.ShowSuccess($"Automated restore drill PASSED for '{DrillDatabaseName}' in {drillResult.RtoSeconds:F1}s (CHECKDB verified)!", "Drill Passed");
                if (_historyRepo != null)
                {
                    await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "SUCCESS", drillResult.RtoSeconds, null, $"CHECKDB Verified in {drillResult.RtoSeconds:F1}s");
                }
            }
            else
            {
                NotificationHub.ShowError($"Automated restore drill FAILED: {drillResult.ErrorMessage}", "Drill Failed");
                if (_historyRepo != null)
                {
                    await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "FAILED", drillResult.RtoSeconds, drillResult.ErrorMessage, drillResult.ErrorMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            DrillProgressMessage = "Drill cancelled.";
            if (_historyRepo != null)
            {
                await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "CANCELLED", sw.Elapsed.TotalSeconds, "Cancelled by user");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            DrillLastOutcomeDisplay = $"FAILED: {ex.Message}";
            DrillLastPassed = false;
            NotificationHub.ShowError($"Drill error: {ex.Message}", "Drill Error");
            if (_historyRepo != null)
            {
                await _historyRepo.UpdateRecoveryOperationAsync(opRecord.OperationId, "FAILED", sw.Elapsed.TotalSeconds, ex.Message, ex.ToString());
            }
        }
        finally
        {
            IsRunningDrill = false;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    public void CancelRestoreDrill()
    {
        _drillCts?.Cancel();
    }

    // =========================================================================
    // 5. RECOVERY HISTORY (§6)
    // =========================================================================
    public async Task LoadHistoryAsync()
    {
        if (_historyRepo == null) return;

        try
        {
            var records = await _historyRepo.GetRecoveryHistoryAsync(100);
            HistoryRecords.Clear();
            foreach (var r in records)
            {
                HistoryRecords.Add(r);
            }

            ApplyHistoryFilter();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load recovery history: {Message}", ex.Message);
        }
    }

    [RelayCommand]
    public void FilterHistory(string type)
    {
        HistoryTypeFilter = type;
        ApplyHistoryFilter();
    }

    partial void OnHistoryStatusFilterChanged(string value) => ApplyHistoryFilter();
    partial void OnHistorySearchQueryChanged(string value) => ApplyHistoryFilter();

    public void RefreshHistoryFilter() => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        FilteredHistoryRecords.Clear();
        var q = HistorySearchQuery?.Trim();

        foreach (var r in HistoryRecords)
        {
            if (!string.Equals(HistoryTypeFilter, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.RecoveryType, HistoryTypeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(HistoryStatusFilter, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.Status, HistoryStatusFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(q))
            {
                bool matches = r.OperationId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               r.SourceBackup.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               r.TargetName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               r.Provider.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                               (r.ErrorMessage?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
                if (!matches) continue;
            }

            FilteredHistoryRecords.Add(r);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
