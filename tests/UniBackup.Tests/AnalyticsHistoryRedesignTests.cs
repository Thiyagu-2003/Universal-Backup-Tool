using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Storage;
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class AnalyticsHistoryRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteHistoryRepository _repo;
    private readonly CredentialProtector _crypto;
    private readonly ConfigurationService _configService;

    public AnalyticsHistoryRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_analytics_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "UniBackup.db");
        _repo = new SqliteHistoryRepository(_dbPath);
        _crypto = new CredentialProtector();
        _configService = new ConfigurationService(_crypto, _tempDir);
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

    [Fact]
    public async Task SqliteHistoryRepository_DateRangeFilter_ReturnsMatchingRuns()
    {
        var now = DateTime.UtcNow;
        var runOld = new RunHistoryRecord
        {
            RunId = Guid.NewGuid().ToString(),
            JobId = "job1",
            JobName = "Old Backup",
            Timestamp = now.AddDays(-40),
            Status = "SUCCESS",
            ArchiveSizeBytes = 1024 * 1024 * 100 // 100 MB
        };
        var runRecent = new RunHistoryRecord
        {
            RunId = Guid.NewGuid().ToString(),
            JobId = "job2",
            JobName = "Recent Backup",
            Timestamp = now.AddDays(-2),
            Status = "SUCCESS",
            ArchiveSizeBytes = 1024 * 1024 * 200 // 200 MB
        };
        var runToday = new RunHistoryRecord
        {
            RunId = Guid.NewGuid().ToString(),
            JobId = "job3",
            JobName = "Today Backup",
            Timestamp = now,
            Status = "FAILED",
            ArchiveSizeBytes = 0
        };

        await _repo.RecordRunAsync(runOld);
        await _repo.RecordRunAsync(runRecent);
        await _repo.RecordRunAsync(runToday);

        // Filter last 7 days
        var fromDate = now.AddDays(-7);
        var toDate = now.AddDays(1);
        var filteredRuns = await _repo.GetRunsAsync(fromDate: fromDate, toDate: toDate);

        Assert.Equal(2, filteredRuns.Count);
        Assert.DoesNotContain(filteredRuns, r => r.JobName == "Old Backup");
        Assert.Contains(filteredRuns, r => r.JobName == "Recent Backup");
        Assert.Contains(filteredRuns, r => r.JobName == "Today Backup");
    }

    [Fact]
    public void AnalyticsViewModel_DatePresets_SetsExpectedDates()
    {
        var vm = new AnalyticsViewModel(
            _configService,
            () => new AppConfig(),
            pipeline: null,
            schedulerService: null,
            diagnosticsService: null);

        // Today preset
        vm.SelectPreset("Today");
        Assert.True(vm.IsPresetToday);
        Assert.Equal(DateTime.Today, vm.StartDate.Date);
        Assert.Equal(DateTime.Today, vm.EndDate.Date);

        // Last 7 days preset
        vm.SelectPreset("Last7Days");
        Assert.True(vm.IsPreset7Days);
        Assert.Equal(DateTime.Today.AddDays(-7), vm.StartDate.Date);

        // Last 30 days preset
        vm.SelectPreset("Last30Days");
        Assert.True(vm.IsPreset30Days);
        Assert.Equal(DateTime.Today.AddDays(-30), vm.StartDate.Date);

        // Last 90 days preset
        vm.SelectPreset("Last90Days");
        Assert.True(vm.IsPreset90Days);
        Assert.Equal(DateTime.Today.AddDays(-90), vm.StartDate.Date);

        // Custom preset
        vm.SelectPreset("Custom");
        Assert.True(vm.IsPresetCustom);
    }

    [Fact]
    public void AnalyticsViewModel_DeltaCalculation_ComputesPercentageCorrectly()
    {
        // Positive delta: from 10 to 15 (+50%)
        var deltaPositive = AnalyticsViewModel.ComputeDelta(15, 10);
        Assert.Contains("+50.0%", deltaPositive);

        // Negative delta: from 20 to 10 (-50%)
        var deltaNegative = AnalyticsViewModel.ComputeDelta(10, 20);
        Assert.Contains("-50.0%", deltaNegative);

        // No change: from 10 to 10 (0.0%)
        var deltaZero = AnalyticsViewModel.ComputeDelta(10, 10);
        Assert.Contains("0.0%", deltaZero);

        // From 0 to 5 (+100.0%)
        var deltaFromZero = AnalyticsViewModel.ComputeDelta(5, 0);
        Assert.Contains("+100.0%", deltaFromZero);

        // Both zero (0.0%)
        var deltaBothZero = AnalyticsViewModel.ComputeDelta(0, 0);
        Assert.Contains("0.0%", deltaBothZero);
    }

    [Fact]
    public async Task AnalyticsViewModel_RefreshAsync_ComputesKPIsAndActivityRows()
    {
        var config = new AppConfig();
        await _configService.SaveConfigAsync(config);

        var now = DateTime.UtcNow;
        var r1 = new RunHistoryRecord
        {
            RunId = "r1",
            JobId = "j1",
            JobName = "Daily SQL",
            Kind = "sqlserver",
            Timestamp = now.AddDays(-1),
            Status = "SUCCESS",
            ArchiveSizeBytes = 1024 * 1024 * 500, // 500 MB
            DurationSeconds = 45.2,
            SourceFolder = "SQL:FULL:AdventureWorks",
            SqlDatabases = [new SqlDatabaseRunResult { Database = "AdventureWorks", BackupType = SqlBackupType.Full, Status = "SUCCESS" }]
        };
        var r2 = new RunHistoryRecord
        {
            RunId = "r2",
            JobId = "j2",
            JobName = "Docs Backup",
            Kind = "folder",
            Timestamp = now.AddDays(-2),
            Status = "FAILED",
            ArchiveSizeBytes = 0,
            DurationSeconds = 12.0,
            ErrorMessage = "Disk out of space",
            SourceFolder = @"C:\Data\Docs"
        };
        var r3 = new RunHistoryRecord
        {
            RunId = "r3",
            JobId = "j3",
            JobName = "IIS Portal",
            Kind = "iis",
            Timestamp = now.AddDays(-3),
            Status = "PARTIAL",
            ArchiveSizeBytes = 1024 * 1024 * 250, // 250 MB
            DurationSeconds = 30.1,
            SourceFolder = "IIS:Portal"
        };

        await _configService.HistoryRepository.RecordRunAsync(r1);
        await _configService.HistoryRepository.RecordRunAsync(r2);
        await _configService.HistoryRepository.RecordRunAsync(r3);

        var vm = new AnalyticsViewModel(
            _configService,
            () => config,
            pipeline: null,
            schedulerService: null,
            diagnosticsService: null);

        vm.SelectPreset("Last7Days");
        await vm.RefreshAsync();

        // 3 Total Backups
        Assert.Equal(3, vm.TotalBackupsCount);
        Assert.Equal(1, vm.SuccessfulCount);
        Assert.Equal(1, vm.FailedCount);
        Assert.Equal(33.3, vm.SuccessRate, 1);
        Assert.Equal(66.7, vm.FailureRate, 1); // 1 failed + 1 partial = 2/3 = 66.7%
        Assert.Equal(750 * 1024 * 1024, vm.TotalDataBytes);
        Assert.Contains("MB", vm.TotalDataDisplay);

        // Verification of rows
        Assert.Equal(3, vm.FilteredActivityRows.Count);
        Assert.Contains(vm.FilteredActivityRows, r => r.JobName == "Daily SQL" && r.BackupTypeDisplay == "SQL (Full)");
        Assert.Contains(vm.FilteredActivityRows, r => r.JobName == "Docs Backup" && r.BackupTypeDisplay == "Folder");
        Assert.Contains(vm.FilteredActivityRows, r => r.JobName == "IIS Portal" && r.BackupTypeDisplay == "IIS Site");
    }

    [Fact]
    public async Task AnalyticsViewModel_SearchAndStatusFilter_FiltersRows()
    {
        var config = new AppConfig();
        await _configService.SaveConfigAsync(config);

        var now = DateTime.UtcNow;
        var r1 = new RunHistoryRecord { RunId = "1", JobName = "Finance DB", Status = "SUCCESS", Timestamp = now };
        var r2 = new RunHistoryRecord { RunId = "2", JobName = "HR Files", Status = "FAILED", Timestamp = now };
        var r3 = new RunHistoryRecord { RunId = "3", JobName = "Finance Logs", Status = "PARTIAL", Timestamp = now };

        await _configService.HistoryRepository.RecordRunAsync(r1);
        await _configService.HistoryRepository.RecordRunAsync(r2);
        await _configService.HistoryRepository.RecordRunAsync(r3);

        var vm = new AnalyticsViewModel(
            _configService,
            () => config,
            pipeline: null,
            schedulerService: null,
            diagnosticsService: null);

        await vm.RefreshAsync();
        Assert.Equal(3, vm.FilteredActivityRows.Count);

        // Filter by Status: FAILED
        vm.SetStatusFilter("FAILED");
        Assert.Single(vm.FilteredActivityRows);
        Assert.Equal("HR Files", vm.FilteredActivityRows[0].JobName);

        // Filter by Status: All + Search Query "Finance"
        vm.SetStatusFilter("ALL");
        vm.SearchQuery = "Finance";
        Assert.Equal(2, vm.FilteredActivityRows.Count);
        Assert.DoesNotContain(vm.FilteredActivityRows, r => r.JobName == "HR Files");
    }

    [Fact]
    public async Task AnalyticsViewModel_Pagination_NavigatesPagesCorrectly()
    {
        var config = new AppConfig();
        await _configService.SaveConfigAsync(config);

        var now = DateTime.UtcNow;
        // Generate 25 records (with PageSize = 15, there are 2 pages)
        for (int i = 1; i <= 25; i++)
        {
            await _configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
            {
                RunId = $"run_{i}",
                JobName = $"Job {i:D2}",
                Status = "SUCCESS",
                Timestamp = now.AddMinutes(-i)
            });
        }

        var vm = new AnalyticsViewModel(
            _configService,
            () => config,
            pipeline: null,
            schedulerService: null,
            diagnosticsService: null);

        await vm.RefreshAsync();

        Assert.Equal(25, vm.FilteredActivityRows.Count);
        Assert.Equal(2, vm.TotalPages);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(15, vm.PagedActivityRows.Count);
        Assert.False(vm.CanGoPrev);
        Assert.True(vm.CanGoNext);

        // Navigate to Next Page
        vm.NextPage();
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(10, vm.PagedActivityRows.Count);
        Assert.True(vm.CanGoPrev);
        Assert.False(vm.CanGoNext);

        // Navigate to First Page
        vm.FirstPage();
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(15, vm.PagedActivityRows.Count);
    }

    [Fact]
    public async Task AnalyticsViewModel_BackupTypeBreakdown_GroupsAccurately()
    {
        var config = new AppConfig();
        await _configService.SaveConfigAsync(config);

        var now = DateTime.UtcNow;
        await _configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
        {
            RunId = "1",
            JobName = "J1",
            Kind = "sqlserver",
            SqlDatabases = [new SqlDatabaseRunResult { Database = "DB1", BackupType = SqlBackupType.Full, Status = "SUCCESS" }],
            Timestamp = now
        });
        await _configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
        {
            RunId = "2",
            JobName = "J2",
            Kind = "sqlserver",
            SqlDatabases = [new SqlDatabaseRunResult { Database = "DB1", BackupType = SqlBackupType.Diff, Status = "SUCCESS" }],
            Timestamp = now
        });
        await _configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
        {
            RunId = "3",
            JobName = "J3",
            Kind = "sqlserver",
            SqlDatabases = [new SqlDatabaseRunResult { Database = "DB1", BackupType = SqlBackupType.Log, Status = "SUCCESS" }],
            Timestamp = now
        });
        await _configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
        {
            RunId = "4",
            JobName = "J4",
            Kind = "folder",
            Timestamp = now
        });
        await _configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
        {
            RunId = "5",
            JobName = "J5",
            Kind = "iis",
            Timestamp = now
        });

        var vm = new AnalyticsViewModel(
            _configService,
            () => config,
            pipeline: null,
            schedulerService: null,
            diagnosticsService: null);

        await vm.RefreshAsync();

        Assert.Equal(5, vm.BackupTypeSlices.Count);
        var fullSlice = vm.BackupTypeSlices.First(s => s.Name == "Full Backup");
        var diffSlice = vm.BackupTypeSlices.First(s => s.Name == "Differential");
        var logSlice = vm.BackupTypeSlices.First(s => s.Name == "Transaction Log");
        var folderSlice = vm.BackupTypeSlices.First(s => s.Name == "Files & Folders");
        var iisSlice = vm.BackupTypeSlices.First(s => s.Name == "IIS Sites");

        Assert.Equal(1, fullSlice.Count);
        Assert.Equal(1, diffSlice.Count);
        Assert.Equal(1, logSlice.Count);
        Assert.Equal(1, folderSlice.Count);
        Assert.Equal(1, iisSlice.Count);

        Assert.Equal(20.0, fullSlice.Percentage);
        Assert.Equal(20.0, diffSlice.Percentage);
    }
}
