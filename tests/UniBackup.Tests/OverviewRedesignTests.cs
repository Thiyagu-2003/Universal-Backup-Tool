using System.Collections.ObjectModel;
using System.IO;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.Infrastructure.Windows;
using UniBackup.UI.Services;
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class OverviewRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CredentialProtector _crypto;
    private readonly ConfigurationService _configService;
    private readonly MainViewModel _vm;

    public OverviewRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_overview_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _crypto = new CredentialProtector();
        _configService = new ConfigurationService(_crypto, _tempDir);

        var archiveService = new HighSpeedZipArchiveService();
        var storageProviders = new IStorageProvider[] { new LocalStorageProvider() };
        var emailService = new EmailNotificationService();
        var webhookService = new WebhookNotificationService();
        var schedulerService = new WinTaskSchedulerService();
        var iisService = new IisSiteDiscoveryService();
        var sqlBackup = new SqlServerBackupService(_crypto);
        var pipeline = new BackupPipeline(archiveService, storageProviders, emailService, webhookService, _configService, sqlBackup);
        var batchRunner = new BatchBackupRunner(pipeline, _configService, emailService);
        var recoveryService = new RecoveryService(storageProviders, archiveService);
        var diagnostics = new ServerDiagnosticsService(storageProviders, emailService, iisService, _configService);

        _vm = new MainViewModel(
            _configService,
            pipeline,
            batchRunner,
            recoveryService,
            diagnostics,
            schedulerService,
            iisService,
            emailService);
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
    public void OverviewHeader_DatePresets_UpdatesLabelsAndPresets()
    {
        _vm.SelectOverviewPreset("Last7Days");
        Assert.Equal("Last7Days", _vm.OverviewDatePreset);
        Assert.True(_vm.IsOverviewPreset7Days);
        Assert.False(_vm.IsOverviewPreset30Days);
        Assert.Contains("7 days", _vm.OverviewDateRangeDisplay);

        _vm.SelectOverviewPreset("Last90Days");
        Assert.Equal("Last90Days", _vm.OverviewDatePreset);
        Assert.True(_vm.IsOverviewPreset90Days);
        Assert.False(_vm.IsOverviewPreset7Days);
        Assert.Contains("90 days", _vm.OverviewDateRangeDisplay);

        _vm.SelectOverviewPreset("Last30Days");
        Assert.Equal("Last30Days", _vm.OverviewDatePreset);
        Assert.True(_vm.IsOverviewPreset30Days);
        Assert.Contains("30 days", _vm.OverviewDateRangeDisplay);
    }

    [Fact]
    public void KpiCards_ComputesConfiguredActiveAndDisabledJobsCorrectly()
    {
        _vm.Jobs.Clear();
        _vm.Jobs.Add(new JobCardViewModel(new BackupJob { Name = "Job 1", Enabled = true }));
        _vm.Jobs.Add(new JobCardViewModel(new BackupJob { Name = "Job 2", Enabled = true }));
        _vm.Jobs.Add(new JobCardViewModel(new BackupJob { Name = "Job 3", Enabled = false }));

        _vm.TotalJobsCount = _vm.Jobs.Count;
        _vm.EnabledJobsCount = _vm.Jobs.Count(j => j.Enabled);

        Assert.Equal(3, _vm.TotalJobsCount);
        Assert.Equal(2, _vm.EnabledJobsCount);
        Assert.Equal(1, _vm.DisabledJobsCount);
    }

    [Fact]
    public void KpiCards_NeedsAttention_FormatsBreakdownAndBadge()
    {
        _vm.FailedRunsCount = 2;
        _vm.PartialRunsCount = 1;
        _vm.StaleJobsCount = 1;
        _vm.AttentionJobsCount = 3;

        _vm.UpdateOverviewData();

        Assert.Contains("2 failed", _vm.AttentionSummaryBreakdown);
        Assert.Contains("1 partial", _vm.AttentionSummaryBreakdown);
        Assert.Contains("1 stale", _vm.AttentionSummaryBreakdown);
        Assert.Contains("3 jobs require review", _vm.AttentionBadgeText);
    }

    [Fact]
    public void BackupDistribution_ClassifiesTypes_Correctly()
    {
        _vm.HistoryRecords.Clear();
        var now = DateTime.UtcNow;

        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "SQL Prod",
            Kind = "sqlserver",
            Timestamp = now.AddDays(-1),
            SqlDatabases = [new SqlDatabaseRunResult { BackupType = SqlBackupType.Full }]
        });
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "SQL Diff",
            Kind = "sqlserver",
            Timestamp = now.AddDays(-2),
            SqlDatabases = [new SqlDatabaseRunResult { BackupType = SqlBackupType.Diff }]
        });
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "SQL Log",
            Kind = "sqlserver",
            Timestamp = now.AddDays(-3),
            SqlDatabases = [new SqlDatabaseRunResult { BackupType = SqlBackupType.Log }]
        });
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "Web Assets",
            Kind = "folder",
            Timestamp = now.AddDays(-4)
        });
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "IIS Portal",
            Kind = "iis",
            Timestamp = now.AddDays(-5)
        });

        _vm.UpdateOverviewData();

        Assert.True(_vm.HasOverviewTypeDistribution);
        Assert.Equal(5, _vm.OverviewTotalTypeCount);

        var fullSlice = _vm.OverviewTypeSlices.FirstOrDefault(s => s.Name == "Full Backup");
        var diffSlice = _vm.OverviewTypeSlices.FirstOrDefault(s => s.Name == "Differential");
        var logSlice = _vm.OverviewTypeSlices.FirstOrDefault(s => s.Name == "Transaction Log");
        var folderSlice = _vm.OverviewTypeSlices.FirstOrDefault(s => s.Name == "Files & Folders");
        var iisSlice = _vm.OverviewTypeSlices.FirstOrDefault(s => s.Name == "IIS Sites");

        Assert.NotNull(fullSlice);
        Assert.Equal(1, fullSlice.Count);
        Assert.Equal(20.0, fullSlice.Percentage);

        Assert.NotNull(diffSlice);
        Assert.Equal(1, diffSlice.Count);

        Assert.NotNull(logSlice);
        Assert.Equal(1, logSlice.Count);

        Assert.NotNull(folderSlice);
        Assert.Equal(1, folderSlice.Count);

        Assert.NotNull(iisSlice);
        Assert.Equal(1, iisSlice.Count);
    }

    [Fact]
    public void RecentActivityTable_SearchFilterAndSorting_OperatesCorrectly()
    {
        _vm.HistoryRecords.Clear();
        var now = DateTime.UtcNow;

        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "Production SQL Server",
            Kind = "sqlserver",
            Timestamp = now.AddHours(-1),
            Status = "SUCCESS",
            ArchiveSizeBytes = 1024 * 1024 * 500, // 500 MB
            DurationSeconds = 45.0
        });
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "Web Assets Folder",
            Kind = "folder",
            Timestamp = now.AddHours(-3),
            Status = "FAILED",
            ArchiveSizeBytes = 1024 * 1024 * 50, // 50 MB
            DurationSeconds = 12.0
        });
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobName = "Finance Logs",
            Kind = "folder",
            Timestamp = now.AddHours(-5),
            Status = "PARTIAL",
            ArchiveSizeBytes = 1024 * 1024 * 80, // 80 MB
            DurationSeconds = 20.0
        });

        _vm.UpdateOverviewData();

        // 1. Initial State: 3 items
        Assert.Equal(3, _vm.OverviewFilteredRows.Count);

        // 2. Search Filter for "SQL"
        _vm.OverviewSearchQuery = "SQL";
        Assert.Single(_vm.OverviewFilteredRows);
        Assert.Equal("Production SQL Server", _vm.OverviewFilteredRows[0].JobName);

        // 3. Clear Search & Apply Status Filter "FAILED"
        _vm.OverviewSearchQuery = "";
        _vm.SetOverviewStatusFilter("FAILED");
        Assert.Single(_vm.OverviewFilteredRows);
        Assert.Equal("Web Assets Folder", _vm.OverviewFilteredRows[0].JobName);

        // 4. Reset Status Filter & Sort by Size Ascending
        _vm.SetOverviewStatusFilter("ALL");
        _vm.SortOverviewBy("Size"); // toggles to Size desc
        _vm.SortOverviewBy("Size"); // toggles to Size asc
        Assert.Equal(3, _vm.OverviewFilteredRows.Count);
        Assert.Equal("Web Assets Folder", _vm.OverviewFilteredRows[0].JobName); // 50 MB is smallest
    }

    [Fact]
    public void RecentActivityTable_Pagination_TraversesPagesCorrectly()
    {
        _vm.HistoryRecords.Clear();
        var now = DateTime.UtcNow;

        // Generate 20 runs
        for (int i = 0; i < 20; i++)
        {
            _vm.HistoryRecords.Add(new RunHistoryRecord
            {
                JobName = $"Job {i + 1}",
                Kind = "folder",
                Timestamp = now.AddMinutes(-i * 10),
                Status = "SUCCESS",
                ArchiveSizeBytes = 1024 * 1024 * (i + 1)
            });
        }

        _vm.OverviewPageSize = 8;
        _vm.UpdateOverviewData();

        Assert.Equal(20, _vm.OverviewFilteredRows.Count);
        Assert.Equal(3, _vm.OverviewTotalPages); // 8 + 8 + 4 = 3 pages
        Assert.Equal(1, _vm.OverviewCurrentPage);
        Assert.Equal(8, _vm.OverviewPagedRows.Count);
        Assert.False(_vm.CanOverviewPrevPage);
        Assert.True(_vm.CanOverviewNextPage);

        // Next page
        _vm.OverviewNextPage();
        Assert.Equal(2, _vm.OverviewCurrentPage);
        Assert.Equal(8, _vm.OverviewPagedRows.Count);
        Assert.True(_vm.CanOverviewPrevPage);
        Assert.True(_vm.CanOverviewNextPage);

        // Last page
        _vm.OverviewLastPage();
        Assert.Equal(3, _vm.OverviewCurrentPage);
        Assert.Equal(4, _vm.OverviewPagedRows.Count);
        Assert.True(_vm.CanOverviewPrevPage);
        Assert.False(_vm.CanOverviewNextPage);

        // First page
        _vm.OverviewFirstPage();
        Assert.Equal(1, _vm.OverviewCurrentPage);
        Assert.Equal(8, _vm.OverviewPagedRows.Count);
    }

    [Fact]
    public void ExecutionDetails_ViewAndClose_ControlsFlyoutState()
    {
        var row = new ActivityRowVm
        {
            RunId = "run-1234",
            JobName = "Critical DB Backup",
            Status = "SUCCESS",
            ArchiveSizeBytes = 1024 * 1024 * 1024
        };

        Assert.False(_vm.IsOverviewDetailsOpen);
        Assert.Null(_vm.OverviewSelectedRow);

        _vm.ViewOverviewDetails(row);

        Assert.True(_vm.IsOverviewDetailsOpen);
        Assert.NotNull(_vm.OverviewSelectedRow);
        Assert.Equal("Critical DB Backup", _vm.OverviewSelectedRow.JobName);

        _vm.CloseOverviewDetails();

        Assert.False(_vm.IsOverviewDetailsOpen);
        Assert.Null(_vm.OverviewSelectedRow);
    }

    [Fact]
    public void NavigationCommands_SwitchActiveTabProperly()
    {
        _vm.NavigateToJobs();
        Assert.Equal("Backups", _vm.ActiveTab);

        _vm.NavigateToAnalytics();
        Assert.Equal("Summary", _vm.ActiveTab);

        _vm.NavigateToCloudRecovery();
        Assert.Equal("Recovery", _vm.ActiveTab);

        _vm.NavigateToSettingsDiagnostics();
        Assert.Equal("Settings", _vm.ActiveTab);

        _vm.NavigateToFullHistory();
        Assert.Equal("Summary", _vm.ActiveTab);

        _vm.NavigateToAttentionJobs();
        Assert.Equal("Backups", _vm.ActiveTab);
    }
}
