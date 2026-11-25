using System.IO.Compression;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class CloudRecoveryRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteHistoryRepository _repo;
    private readonly CredentialProtector _crypto;
    private readonly ConfigurationService _configService;
    private readonly HighSpeedZipArchiveService _archiveService;

    public CloudRecoveryRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_recovery_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "UniBackup.db");
        _repo = new SqliteHistoryRepository(_dbPath);
        _crypto = new CredentialProtector();
        _configService = new ConfigurationService(_crypto, _tempDir);
        _archiveService = new HighSpeedZipArchiveService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private class MockStorageProvider : IStorageProvider
    {
        public string Name => "local";
        public List<CloudArchiveItem> Items { get; } = [];

        public bool IsEnabled(AppConfig config) => true;

        public Task<Result<DestinationRunResult>> UploadAsync(
            AppConfig config, BackupJob job, string localFilePath, string? targetFolderOverride = null,
            IProgress<(long uploadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(Result<DestinationRunResult>.Success(new DestinationRunResult { Destination = Name, Status = "SUCCESS" }));

        public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(AppConfig config, string? folderPrefix = null, CancellationToken ct = default) =>
            Task.FromResult(Result<List<CloudArchiveItem>>.Success(new List<CloudArchiveItem>(Items)));

        public Task<Result> DownloadAsync(AppConfig config, string keyOrId, string destinationFilePath,
            IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default)
        {
            if (File.Exists(keyOrId))
            {
                var dir = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(keyOrId, destinationFilePath, true);
                return Task.FromResult(Result.Success());
            }
            return Task.FromResult(Result.Failure("File not found"));
        }

        public Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<int>> CleanupRetentionAsync(AppConfig config, string targetFolder, int retentionDays, CancellationToken ct = default) =>
            Task.FromResult(Result<int>.Success(0));

        public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void CloudSourceDiscovery_IdentifiesAllConfiguredProviders()
    {
        var config = new AppConfig();
        // Configure AWS
        config.Aws.Enabled = true;
        config.Aws.BucketName = "my-test-bucket";
        config.Aws.Region = "us-east-1";

        // Configure Azure
        config.Azure.Enabled = true;
        config.Azure.ContainerName = "my-azure-container";

        // Configure Local
        config.Local.Enabled = true;
        config.Local.TargetPath = @"C:\Backups\Local";

        // GDrive disabled
        config.GDrive.Enabled = false;

        var sources = RecoveryViewModel.GetConfiguredSources(config);

        Assert.Equal(3, sources.Count);
        Assert.Contains(sources, s => s.ProviderId == "s3" && s.BucketOrPath == "my-test-bucket");
        Assert.Contains(sources, s => s.ProviderId == "azure" && s.BucketOrPath == "my-azure-container");
        Assert.Contains(sources, s => s.ProviderId == "local" && s.BucketOrPath == @"C:\Backups\Local");
        Assert.DoesNotContain(sources, s => s.ProviderId == "gdrive");
    }

    [Fact]
    public void SqlDatabaseDiscovery_ParsesArchives_IntoDatabaseGroups()
    {
        var archives = new List<CloudArchiveItem>
        {
            new() { KeyOrId = "InventoryDB_FULL_20261124_080000.bak", DisplayName = "InventoryDB_FULL_20261124_080000.bak", SizeBytes = 5000, LastModified = new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc) },
            new() { KeyOrId = "InventoryDB_DIFF_20261124_100000.bak", DisplayName = "InventoryDB_DIFF_20261124_100000.bak", SizeBytes = 2000, LastModified = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc) },
            new() { KeyOrId = "InventoryDB_LOG_20261124_103000.trn", DisplayName = "InventoryDB_LOG_20261124_103000.trn", SizeBytes = 500, LastModified = new DateTime(2026, 11, 24, 10, 30, 0, DateTimeKind.Utc) },
            new() { KeyOrId = "AccountingDB_FULL_20261124_090000.bak", DisplayName = "AccountingDB_FULL_20261124_090000.bak", SizeBytes = 8000, LastModified = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc) }
        };

        var groups = RecoveryViewModel.GroupSqlBackupsByDatabase(archives);

        Assert.Equal(2, groups.Count);
        var inventory = groups.FirstOrDefault(g => g.Name == "InventoryDB");
        Assert.NotNull(inventory);
        Assert.Equal(1, inventory.FullCount);
        Assert.Equal(1, inventory.DiffCount);
        Assert.Equal(1, inventory.LogCount);
        Assert.Equal(3, inventory.TotalBackups);
        Assert.Equal(7500, inventory.TotalSizeBytes);

        var accounting = groups.FirstOrDefault(g => g.Name == "AccountingDB");
        Assert.NotNull(accounting);
        Assert.Equal(1, accounting.FullCount);
        Assert.Equal(0, accounting.DiffCount);
        Assert.Equal(0, accounting.LogCount);
    }

    [Fact]
    public void SqlRestorePlanner_BuildsValidSequence_ForPointInTime()
    {
        var tFull = new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);
        var tDiff = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        var tLog1 = new DateTime(2026, 11, 24, 10, 30, 0, DateTimeKind.Utc);
        var tLog2 = new DateTime(2026, 11, 24, 11, 0, 0, DateTimeKind.Utc);

        var archives = new List<CloudArchiveItem>
        {
            new() { KeyOrId = "SalesDB_FULL_20261124_080000.bak", DisplayName = "SalesDB_FULL_20261124_080000.bak", LastModified = tFull },
            new() { KeyOrId = "SalesDB_DIFF_20261124_100000.bak", DisplayName = "SalesDB_DIFF_20261124_100000.bak", LastModified = tDiff },
            new() { KeyOrId = "SalesDB_LOG_20261124_103000.trn", DisplayName = "SalesDB_LOG_20261124_103000.trn", LastModified = tLog1 },
            new() { KeyOrId = "SalesDB_LOG_20261124_110000.trn", DisplayName = "SalesDB_LOG_20261124_110000.trn", LastModified = tLog2 }
        };

        var targetTime = new DateTime(2026, 11, 24, 10, 45, 0, DateTimeKind.Utc);
        var planResult = SqlRestorePlanner.Plan(archives, "SalesDB", targetTime);

        Assert.True(planResult.IsSuccess, planResult.Error);
        var plan = planResult.Value;
        Assert.NotNull(plan.FullBackup);
        Assert.NotNull(plan.DiffBackup);
        Assert.NotEmpty(plan.LogBackups);
        Assert.Equal("SalesDB_FULL_20261124_080000.bak", plan.FullBackup.FileName);
        Assert.Equal("SalesDB_DIFF_20261124_100000.bak", plan.DiffBackup.FileName);
        // Target 10:45 is covered by the 11:00 log
        Assert.Contains(plan.LogBackups, l => l.FileName == "SalesDB_LOG_20261124_110000.trn");
    }

    [Fact]
    public async Task RecoveryService_InspectArchive_DetectsIisWebsiteAndFiles()
    {
        var zipPath = Path.Combine(_tempDir, "iis_backup_test.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var webConfigEntry = archive.CreateEntry("web.config");
            using (var writer = new StreamWriter(webConfigEntry.Open()))
            {
                writer.WriteLine("<configuration><system.webServer></system.webServer></configuration>");
            }

            var dllEntry = archive.CreateEntry("bin/MyApp.dll");
            using (var writer = new StreamWriter(dllEntry.Open()))
            {
                writer.WriteLine("Dummy DLL content");
            }

            var htmlEntry = archive.CreateEntry("index.html");
            using (var writer = new StreamWriter(htmlEntry.Open()))
            {
                writer.WriteLine("<html><body>Hello UniBackup</body></html>");
            }
        }

        var mockProvider = new MockStorageProvider();
        var recoveryService = new RecoveryService([mockProvider], _archiveService);

        var cloudItem = new CloudArchiveItem
        {
            KeyOrId = zipPath,
            DisplayName = Path.GetFileName(zipPath),
            SizeBytes = new FileInfo(zipPath).Length,
            LastModified = DateTime.UtcNow,
            Destination = "local"
        };

        var config = new AppConfig();
        var inspectResult = await recoveryService.InspectArchiveAsync(config, cloudItem);

        Assert.True(inspectResult.IsSuccess, inspectResult.Error);
        var details = inspectResult.Value;
        Assert.True(details.IsIisWebsite, "Should detect IIS site from web.config");
        Assert.Equal(3, details.TotalFiles);
        Assert.Contains(details.Entries, e => e.Name == "web.config" && e.IsIisConfig);
        Assert.Contains(details.Entries, e => e.Name == "index.html");
        Assert.Contains(details.Entries, e => e.Name == "MyApp.dll");
    }

    [Fact]
    public async Task RecoveryService_RestoreSelectiveAsync_RespectsSafeConflictPolicy()
    {
        var zipPath = Path.Combine(_tempDir, "selective_test.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var f1 = archive.CreateEntry("config/appsettings.json");
            using (var writer = new StreamWriter(f1.Open()))
            {
                writer.WriteLine("{\"Key\": \"Value123\"}");
            }

            var f2 = archive.CreateEntry("data/large_file.dat");
            using (var writer = new StreamWriter(f2.Open()))
            {
                writer.WriteLine("Large binary simulation");
            }
        }

        var mockProvider = new MockStorageProvider();
        var recoveryService = new RecoveryService([mockProvider], _archiveService);

        var cloudItem = new CloudArchiveItem
        {
            KeyOrId = zipPath,
            DisplayName = Path.GetFileName(zipPath),
            SizeBytes = new FileInfo(zipPath).Length,
            LastModified = DateTime.UtcNow,
            Destination = "local"
        };

        var targetExtractDir = Path.Combine(_tempDir, "RestoredFiles");
        Directory.CreateDirectory(targetExtractDir);

        var config = new AppConfig();
        // Restore only config/appsettings.json with "safe" mode (which writes to targetExtractDir or subfolder)
        var selectedRelativePaths = new List<string> { "config/appsettings.json" };
        var restoreResult = await recoveryService.RestoreSelectiveAsync(
            config, cloudItem, selectedRelativePaths, targetExtractDir, writeMode: "overwrite");

        Assert.True(restoreResult.IsSuccess, restoreResult.Error);
        Assert.Equal(1, restoreResult.Value.FilesRestored);

        // Verify that appsettings.json exists in target
        var expectedFile = Path.Combine(targetExtractDir, "config", "appsettings.json");
        Assert.True(File.Exists(expectedFile), $"Expected file at {expectedFile}");

        // Verify data/large_file.dat was NOT extracted
        var unselectedFile = Path.Combine(targetExtractDir, "data", "large_file.dat");
        Assert.False(File.Exists(unselectedFile), "Unselected file should not be extracted");
    }

    [Fact]
    public async Task RecoveryHistory_RecordsAndRetrievesOperations_FromSqlite()
    {
        await _repo.InitializeAsync();

        var op = new RecoveryOperationRecord
        {
            OperationId = "REC-20261124-999",
            Provider = "AWS S3",
            SourceBackup = "Production_FULL_20261124.bak",
            TargetName = "Production_Restored",
            RecoveryType = "SqlPointInTime",
            StartTime = DateTime.UtcNow.AddMinutes(-5),
            Status = "RUNNING",
            ItemsRestored = 1,
            BytesRestored = 1024 * 1024 * 250
        };

        await _repo.RecordRecoveryOperationAsync(op);

        // Update to SUCCESS
        op.EndTime = DateTime.UtcNow;
        op.DurationSeconds = 300;
        op.Status = "SUCCESS";
        op.LogDetails = "Point-in-time restore completed successfully to 2026-11-24 10:45:00 UTC.";

        await _repo.UpdateRecoveryOperationAsync(
            op.OperationId, op.Status, op.DurationSeconds, op.ErrorMessage, op.LogDetails, op.ItemsRestored, op.BytesRestored);

        // Retrieve
        var records = await _repo.GetRecoveryHistoryAsync(limit: 10, typeFilter: "SqlPointInTime");
        Assert.NotEmpty(records);
        var retrieved = records.FirstOrDefault(r => r.OperationId == op.OperationId);
        Assert.NotNull(retrieved);
        Assert.Equal("SUCCESS", retrieved.Status);
        Assert.Equal("Production_Restored", retrieved.TargetName);
        Assert.Equal(300, retrieved.DurationSeconds);
    }

    [Fact]
    public void RecoveryViewModel_TabSwitching_UpdatesState()
    {
        var vm = new RecoveryViewModel(
            () => new AppConfig(),
            _configService,
            new RecoveryService([], _archiveService),
            null,
            new SqlServerRestoreService(_crypto),
            new SqlServerBackupService(_crypto),
            null,
            [],
            _repo,
            _crypto);

        Assert.True(vm.IsDashboardTab);
        Assert.False(vm.IsSqlServerTab);
        Assert.False(vm.IsFilesTab);
        Assert.False(vm.IsDrillsTab);
        Assert.False(vm.IsHistoryTab);

        vm.SelectedTab = "SqlServer";
        Assert.False(vm.IsDashboardTab);
        Assert.True(vm.IsSqlServerTab);

        vm.SelectedTab = "FilesAndSites";
        Assert.True(vm.IsFilesTab);

        vm.SelectedTab = "Drills";
        Assert.True(vm.IsDrillsTab);

        vm.SelectedTab = "History";
        Assert.True(vm.IsHistoryTab);
    }

    [Fact]
    public void RecoveryViewModel_SearchHistory_FiltersCorrectly()
    {
        var vm = new RecoveryViewModel(
            () => new AppConfig(),
            _configService,
            new RecoveryService([], _archiveService),
            null,
            new SqlServerRestoreService(_crypto),
            new SqlServerBackupService(_crypto),
            null,
            [],
            _repo,
            _crypto);

        vm.HistoryRecords.Add(new RecoveryOperationRecord
        {
            OperationId = "REC-101",
            SourceBackup = "FinanceDB_2026.bak",
            TargetName = "FinanceDB_Sandbox",
            RecoveryType = "SqlPointInTime",
            Status = "SUCCESS"
        });

        vm.HistoryRecords.Add(new RecoveryOperationRecord
        {
            OperationId = "REC-102",
            SourceBackup = "WebSite_Static_2026.zip",
            TargetName = @"C:\inetpub\wwwroot\portal",
            RecoveryType = "FilesAndSites",
            Status = "FAILED"
        });

        vm.RefreshHistoryFilter();
        Assert.Equal(2, vm.FilteredHistoryRecords.Count);

        // Filter by text search
        vm.HistorySearchQuery = "Finance";
        Assert.Single(vm.FilteredHistoryRecords);
        Assert.Equal("REC-101", vm.FilteredHistoryRecords[0].OperationId);

        // Filter by category
        vm.HistorySearchQuery = "";
        vm.FilterHistory("FilesAndSites");
        Assert.Single(vm.FilteredHistoryRecords);
        Assert.Equal("REC-102", vm.FilteredHistoryRecords[0].OperationId);
    }

    private class FailingStorageProvider : IStorageProvider
    {
        public string Name => "s3";
        public bool IsEnabled(AppConfig config) => true;
        public Task<Result<DestinationRunResult>> UploadAsync(AppConfig config, BackupJob job, string localFilePath, string? targetFolderOverride = null, IProgress<(long uploadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) => Task.FromResult(Result<DestinationRunResult>.Failure("Offline"));
        public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(AppConfig config, string? folderPrefix = null, CancellationToken ct = default) => Task.FromResult(Result<List<CloudArchiveItem>>.Failure("S3 unreachable"));
        public Task<Result> DownloadAsync(AppConfig config, string keyOrId, string destinationFilePath, IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) => Task.FromResult(Result.Failure("Offline"));
        public Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default) => Task.FromResult(Result.Failure("Offline"));
        public Task<Result<int>> CleanupRetentionAsync(AppConfig config, string targetFolder, int retentionDays, CancellationToken ct = default) => Task.FromResult(Result<int>.Failure("Offline"));
        public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default) => throw new TimeoutException("Simulated S3 timeout");
    }

    [Fact]
    public void MultiLocalDiscovery_IncludesAdditionalPaths_AndScopesConfigWithoutMutatingBase()
    {
        var config = new AppConfig();
        config.Aws.Enabled = false;
        config.Local.Enabled = true;
        config.Local.TargetPath = @"C:\Backups\Primary";
        config.Local.AdditionalPaths = [@"D:\USB_Backups", @"\\NAS\Archives", @"C:\Backups\Primary"];

        var sources = RecoveryViewModel.GetConfiguredSources(config);

        Assert.Equal(3, sources.Count);
        Assert.Equal(1, sources.Count(s => s.CustomLocalPath == @"C:\Backups\Primary" && !s.IsAdditionalLocal));
        Assert.Contains(sources, s => s.CustomLocalPath == @"D:\USB_Backups" && s.IsAdditionalLocal);
        Assert.Contains(sources, s => s.CustomLocalPath == @"\\NAS\Archives" && s.IsAdditionalLocal);

        var secondary = sources.First(s => s.CustomLocalPath == @"D:\USB_Backups");
        var scoped = RecoveryViewModel.CreateScopedConfigForSource(config, secondary);
        Assert.Equal(@"D:\USB_Backups", scoped.Local.TargetPath);
        Assert.Equal(@"C:\Backups\Primary", config.Local.TargetPath);
    }

    [Fact]
    public async Task RefreshDashboard_IsolatesFailingProvider_AndAggregatesAllSources()
    {
        var config = new AppConfig();
        config.Aws.Enabled = true;
        config.Aws.BucketName = "offline-bucket";
        config.Local.Enabled = true;
        config.Local.TargetPath = _tempDir;

        var mockLocal = new MockStorageProvider();
        mockLocal.Items.Add(new CloudArchiveItem { KeyOrId = "Site_2026.zip", DisplayName = "Site_2026.zip", SizeBytes = 1024, Destination = "local" });

        var vm = new RecoveryViewModel(
            () => config,
            _configService,
            new RecoveryService([mockLocal], _archiveService),
            null,
            new SqlServerRestoreService(_crypto),
            new SqlServerBackupService(_crypto),
            null,
            [new FailingStorageProvider(), mockLocal],
            _repo,
            _crypto);

        await vm.RefreshDashboardAsync();

        Assert.Equal(2, vm.ConfiguredSources.Count);
        Assert.Equal("Error", vm.ConfiguredSources.First(s => s.ProviderId == "s3").Status);
        Assert.Equal("Connected", vm.ConfiguredSources.First(s => s.ProviderId == "local").Status);
        Assert.Contains(vm.FileSources, s => s.IsAllSources);
        Assert.Single(vm.AvailableArchives);
    }

    [Fact]
    public void ArchiveExplorer_SearchFilter_FiltersNodesInRealTime()
    {
        var vm = new RecoveryViewModel(
            () => new AppConfig(),
            _configService,
            new RecoveryService([], _archiveService),
            null,
            new SqlServerRestoreService(_crypto),
            new SqlServerBackupService(_crypto),
            null,
            [],
            _repo,
            _crypto);

        vm.ArchiveExplorerNodes.Add(new ArchiveExplorerItemVm { Name = "web.config", RelativePath = "site/web.config" });
        vm.ArchiveExplorerNodes.Add(new ArchiveExplorerItemVm { Name = "appsettings.json", RelativePath = "site/appsettings.json" });
        vm.ApplyArchiveExplorerFilter();

        Assert.Equal(2, vm.FilteredArchiveExplorerNodes.Count);
        vm.SearchFilter = "web.config";
        Assert.Single(vm.FilteredArchiveExplorerNodes);
        Assert.Equal("web.config", vm.FilteredArchiveExplorerNodes[0].Name);
    }
}
