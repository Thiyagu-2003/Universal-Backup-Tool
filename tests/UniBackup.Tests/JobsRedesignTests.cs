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
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class JobsRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CredentialProtector _crypto;
    private readonly ConfigurationService _configService;
    private readonly MainViewModel _vm;

    public JobsRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_jobs_test_" + Guid.NewGuid().ToString("N"));
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
        catch
        {
        }
    }

    private static List<BackupJob> CreateSampleJobs() =>
    [
        new BackupJob
        {
            Id = "j1",
            Name = "Alpha SQL Prod",
            Kind = "sqlserver",
            SourceFolder = "localhost\\MSSQLSERVER",
            Enabled = true,
            LastRunStatus = "SUCCESS",
            LastRunTime = DateTime.UtcNow.AddHours(-2),
            Schedule = new JobSchedule
            {
                Freq = "daily",
                Time = "02:00",
                Diff = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 240 },
                Log = new IntervalBackupSchedule { Enabled = true, EveryMinutes = 30 }
            }
        },
        new BackupJob
        {
            Id = "j2",
            Name = "Beta IIS Portal",
            Kind = "folder",
            SourceFolder = "IIS:Default Web Site",
            Enabled = true,
            LastRunStatus = "FAILED",
            LastRunTime = DateTime.UtcNow.AddHours(-5),
            Schedule = new JobSchedule { Freq = "daily", Time = "03:00" }
        },
        new BackupJob
        {
            Id = "j3",
            Name = "Gamma Documents",
            Kind = "folder",
            SourceFolder = @"D:\Data\Docs",
            Enabled = false,
            LastRunStatus = "PARTIAL",
            LastRunTime = DateTime.UtcNow.AddDays(-2),
            Schedule = new JobSchedule { Freq = "manual" }
        }
    ];

    [Fact]
    public void DatePresetSelection_UpdatesFlagsAndActivityChart()
    {
        _vm.HistoryRecords.Clear();
        _vm.HistoryRecords.Add(new RunHistoryRecord { JobId = "j1", JobName = "Alpha", Status = "SUCCESS", Timestamp = DateTime.UtcNow.AddDays(-2) });
        _vm.HistoryRecords.Add(new RunHistoryRecord { JobId = "j2", JobName = "Beta", Status = "FAILED", Timestamp = DateTime.UtcNow.AddDays(-15) });

        _vm.SelectJobsPreset("Last7Days");
        Assert.True(_vm.IsJobsPreset7Days);
        Assert.False(_vm.IsJobsPreset30Days);
        Assert.True(_vm.HasJobsActivityHistory);
        Assert.Equal(3, _vm.JobsActivitySeries.Length);

        _vm.SelectJobsPreset("Last90Days");
        Assert.True(_vm.IsJobsPreset90Days);
        Assert.Contains("90 days", _vm.JobsDateRangeDisplay);
    }

    [Fact]
    public void DistributionDonut_CategorizesSupportedBackupTypesAccurately()
    {
        var jobs = CreateSampleJobs();
        _vm.SetAllJobsCache(jobs);
        _vm.TotalJobsCount = jobs.Count;
        _vm.EnabledJobsCount = jobs.Count(j => j.Enabled);
        _vm.UpdateJobsPageData();

        Assert.True(_vm.HasJobsDistribution);
        Assert.Equal(3, _vm.JobsDistributionTotalCount);
        Assert.Contains(_vm.JobsDistributionSlices, s => s.Name == "Full Backup" && s.Count == 1);
        Assert.Contains(_vm.JobsDistributionSlices, s => s.Name == "Differential" && s.Count == 1);
        Assert.Contains(_vm.JobsDistributionSlices, s => s.Name == "Transaction Log" && s.Count == 1);
        Assert.Contains(_vm.JobsDistributionSlices, s => s.Name == "IIS Website" && s.Count == 1);
        Assert.Contains(_vm.JobsDistributionSlices, s => s.Name == "Folder Backup" && s.Count == 1);
    }

    [Fact]
    public void StatusFiltersAndSearch_FilterJobsAccurately()
    {
        var jobs = CreateSampleJobs();
        _vm.SetAllJobsCache(jobs);
        _vm.HasNoJobs = false;
        _vm.UpdateJobsPageData();

        Assert.Equal(3, _vm.JobsFilteredCount);

        _vm.SetJobsStatusFilter("Enabled");
        Assert.Equal(2, _vm.JobsFilteredCount);

        _vm.SetJobsStatusFilter("Disabled");
        Assert.Equal(1, _vm.JobsFilteredCount);
        Assert.Equal("Gamma Documents", _vm.JobsPagedRows[0].Name);

        _vm.SetJobsStatusFilter("Failed");
        Assert.Equal(1, _vm.JobsFilteredCount);
        Assert.Equal("Beta IIS Portal", _vm.JobsPagedRows[0].Name);

        _vm.SetJobsStatusFilter("All");
        _vm.JobsSearchQuery = "IIS";
        Assert.Equal(1, _vm.JobsFilteredCount);
        Assert.Equal("Beta IIS Portal", _vm.JobsPagedRows[0].Name);

        _vm.ResetJobsFilters();
        Assert.Equal("All", _vm.JobsStatusFilter);
        Assert.Equal("", _vm.JobsSearchQuery);
        Assert.Equal(3, _vm.JobsFilteredCount);
    }

    [Fact]
    public void MultiColumnSorting_OrdersRowsCorrectly()
    {
        var jobs = CreateSampleJobs();
        _vm.SetAllJobsCache(jobs);
        _vm.UpdateJobsPageData();

        _vm.SortJobsBy("Name"); // toggles to descending since Name defaults to ascending
        Assert.Equal("Gamma Documents", _vm.JobsPagedRows[0].Name);

        _vm.SortJobsBy("Name"); // back to ascending
        Assert.Equal("Alpha SQL Prod", _vm.JobsPagedRows[0].Name);

        _vm.SortJobsBy("LastRun"); // defaults to descending (most recent first)
        Assert.Equal("Alpha SQL Prod", _vm.JobsPagedRows[0].Name);
    }

    [Fact]
    public void PaginationAndPageSize_NavigatePagesCorrectly()
    {
        var jobs = Enumerable.Range(1, 12).Select(i => new BackupJob
        {
            Id = $"j{i}",
            Name = $"Job {i:00}",
            Kind = "folder",
            SourceFolder = $@"C:\Data\F{i}",
            Enabled = true
        }).ToList();

        _vm.SetAllJobsCache(jobs);
        _vm.JobsPageSize = 5;
        _vm.UpdateJobsPageData();

        Assert.Equal(3, _vm.JobsTotalPages);
        Assert.Equal(5, _vm.JobsPagedRows.Count);
        Assert.True(_vm.CanJobsNextPage);
        Assert.False(_vm.CanJobsPrevPage);

        _vm.JobsNextPage();
        Assert.Equal(2, _vm.JobsCurrentPage);
        Assert.Equal(5, _vm.JobsPagedRows.Count);

        _vm.JobsLastPage();
        Assert.Equal(3, _vm.JobsCurrentPage);
        Assert.Equal(2, _vm.JobsPagedRows.Count);
        Assert.False(_vm.CanJobsNextPage);
    }

    [Fact]
    public async Task MultiSelectionAndBulkEnableDisable_PersistChanges()
    {
        var jobs = CreateSampleJobs();
        await _configService.SaveJobsAsync(jobs);
        await _vm.RefreshJobsAsync();

        _vm.IsAllCurrentPageSelected = true;
        Assert.Equal(3, _vm.SelectedJobsCount);
        Assert.True(_vm.HasSelectedJobs);

        await _vm.BulkDisableJobsAsync();
        var reloaded = await _configService.LoadJobsAsync();
        Assert.All(reloaded, j => Assert.False(j.Enabled));
        Assert.Equal(0, _vm.SelectedJobsCount);

        _vm.JobsPagedRows[0].IsSelected = true;
        Assert.Equal(1, _vm.SelectedJobsCount);
        await _vm.BulkEnableJobsAsync();

        reloaded = await _configService.LoadJobsAsync();
        Assert.Equal(1, reloaded.Count(j => j.Enabled));
    }

    [Fact]
    public void JobDetailsFlyout_LoadsMatchingHistoryAndClosesCleanly()
    {
        var jobs = CreateSampleJobs();
        _vm.SetAllJobsCache(jobs);
        _vm.HistoryRecords.Add(new RunHistoryRecord
        {
            JobId = "j1",
            JobName = "Alpha SQL Prod",
            Status = "SUCCESS",
            Trigger = "FULL",
            DurationSeconds = 42,
            Timestamp = DateTime.UtcNow.AddHours(-2)
        });
        _vm.UpdateJobsPageData();

        var card = _vm.JobsPagedRows.First(j => j.Id == "j1");
        _vm.OpenJobDetails(card);

        Assert.True(_vm.IsJobDetailsOpen);
        Assert.Equal("Alpha SQL Prod", _vm.SelectedJobDetails?.Name);
        Assert.True(_vm.HasSelectedJobHistory);
        Assert.Single(_vm.SelectedJobRecentHistory);

        _vm.CloseJobDetails();
        Assert.False(_vm.IsJobDetailsOpen);
        Assert.Null(_vm.SelectedJobDetails);
    }
}
