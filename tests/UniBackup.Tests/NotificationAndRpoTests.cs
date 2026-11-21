using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Monitoring;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using Xunit;

namespace UniBackup.Tests;

public class NotificationAndRpoTests
{
    private class FakeNotifier : INotificationService
    {
        public Task<Result> SendRunReportAsync(AppConfig config, BackupJob job, RunHistoryRecord history, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
        public Task<Result> SendBatchReportAsync(AppConfig config, List<RunHistoryRecord> histories, TimeSpan totalDuration, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
        public Task<Result> SendTestNotificationAsync(AppConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public void NotificationTriggerMatrix_Defaults_SuppressesLogSuccess()
    {
        var matrix = new NotificationTriggerMatrix();

        // Defaults: failures & warnings notify, successes do not
        Assert.True(matrix.ShouldNotify(SqlBackupType.Full, "FAILED"));
        Assert.True(matrix.ShouldNotify(SqlBackupType.Diff, "PARTIAL"));
        Assert.True(matrix.ShouldNotify(SqlBackupType.Log, "FAILED"));

        // Frequent log successes should be suppressed by default
        Assert.False(matrix.ShouldNotify(SqlBackupType.Log, "SUCCESS"));
        Assert.False(matrix.ShouldNotify(SqlBackupType.Full, "SUCCESS"));
    }

    [Fact]
    public void NotificationTemplateExpander_ReplacesAllPlaceholders()
    {
        var job = new BackupJob { Name = "Production SQL", Kind = "sqlserver" };
        var history = new RunHistoryRecord
        {
            Status = "SUCCESS",
            DurationSeconds = 125,
            ArchiveSizeBytes = 1024 * 1024 * 50,
            ErrorMessage = null,
            SqlDatabases = [new SqlDatabaseRunResult { Database = "Shop", BackupType = SqlBackupType.Full }]
        };

        var template = "[{server}] Job '{job}' ({type}) finished: {status}. DBs: {databases}. Size: {size}. Duration: {duration}. Errors: {errors}";
        var expanded = NotificationTemplateExpander.Expand(template, job, history, SqlBackupType.Full);

        Assert.Contains("Production SQL", expanded);
        Assert.Contains("FULL", expanded);
        Assert.Contains("SUCCESS", expanded);
        Assert.Contains("Shop", expanded);
        Assert.Contains("50.00 MB", expanded);
        Assert.Contains("Errors: None", expanded);
    }

    [Fact]
    public async Task RpoMonitorService_DetectsBreachedDatabases()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"UniBackup_RpoTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            var configService = new ConfigurationService(new CredentialProtector(), testDir);
            var now = DateTimeOffset.UtcNow;

            // Add a backup for "Shop" that is 3 hours old
            await configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
            {
                JobId = "sql-job",
                Status = "SUCCESS",
                Timestamp = now.AddHours(-3).UtcDateTime,
                SqlDatabases = [new SqlDatabaseRunResult { Database = "Shop", Status = "SUCCESS" }]
            });

            // Add a fresh backup for "Inventory" that is 15 minutes old
            await configService.HistoryRepository.RecordRunAsync(new RunHistoryRecord
            {
                JobId = "sql-job",
                Status = "SUCCESS",
                Timestamp = now.AddMinutes(-15).UtcDateTime,
                SqlDatabases = [new SqlDatabaseRunResult { Database = "Inventory", Status = "SUCCESS" }]
            });

            var monitor = new RpoMonitorService(configService, new FakeNotifier());
            var config = new AppConfig
            {
                Rpo = new RpoSettings { Enabled = true, DefaultRpoMinutes = 60 }
            };

            var job = new BackupJob
            {
                Id = "sql-job",
                Kind = "sqlserver",
                Enabled = true,
                Sql = new SqlServerSource { Databases = ["Shop", "Inventory"] }
            };

            var results = await monitor.CheckRpoComplianceAsync(config, [job], now);

            var shopStatus = results.First(r => r.Database == "Shop");
            var invStatus = results.First(r => r.Database == "Inventory");

            Assert.True(shopStatus.IsBreached); // 180m > 60m target
            Assert.False(invStatus.IsBreached); // 15m <= 60m target
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void PrometheusMetrics_GeneratesValidExpositionText()
    {
        var jobs = new List<BackupJob>
        {
            new() { Name = "Nightly_Backup", Kind = "folder", LastRunStatus = "SUCCESS", LastRunDurationSeconds = 42.1, LastRunSizeBytes = 100000 },
            new() { Name = "Sql_Live", Kind = "sqlserver", LastRunStatus = "FAILED", LastRunDurationSeconds = 12.0, LastRunSizeBytes = 0 }
        };

        var history = new List<RunHistoryRecord>
        {
            new() { Status = "SUCCESS" },
            new() { Status = "SUCCESS" },
            new() { Status = "FAILED" }
        };

        var metrics = PrometheusMetricsService.GenerateMetricsText(jobs, history);

        Assert.Contains("unibackup_job_last_status{job=\"Nightly_Backup\",kind=\"folder\"} 1", metrics);
        Assert.Contains("unibackup_job_last_status{job=\"Sql_Live\",kind=\"sqlserver\"} 0", metrics);
        Assert.Contains("unibackup_total_runs_total{status=\"SUCCESS\"} 2", metrics);
        Assert.Contains("unibackup_total_runs_total{status=\"FAILED\"} 1", metrics);
    }

    [Fact]
    public void HtmlReportGenerator_ProducesFullExecutiveHtml()
    {
        var server = ServerIdentity.Current;
        var jobs = new List<BackupJob>
        {
            new() { Name = "Main Job", Kind = "sqlserver", LastRunStatus = "SUCCESS", LastRunDurationSeconds = 60, LastRunSizeBytes = 5000000 }
        };
        var history = new List<RunHistoryRecord>
        {
            new() { Status = "SUCCESS", ArchiveSizeBytes = 5000000, DurationSeconds = 60 }
        };

        var dbs = new List<SqlDatabaseInfo> { new("Shop", "ONLINE", "FULL", false, false) };
        var html = HtmlReportGenerator.GenerateExecutiveReport(server, jobs, history, dbs);

        Assert.Contains("UniBackup Pro &mdash; Executive Backup &amp; Recovery Audit", html);
        Assert.Contains("100%</div>", html);
        Assert.Contains("Main Job", html);
        Assert.Contains("SQL Server Database Catalog", html);
    }
}
