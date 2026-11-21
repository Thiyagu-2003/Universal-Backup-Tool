using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace UniBackup.Tests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNIBACKUP_TEST_SQL")))
            Skip = "Set UNIBACKUP_TEST_SQL to a local SQL Server instance (e.g. localhost\\SQL2022) to run.";
    }
}

internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

public class SqlServerBackupServiceTests : IAsyncLifetime
{
    private readonly SqlServerBackupService _svc = new(new CredentialProtector());
    private readonly SqlServerSource _src = new()
    {
        Server = Environment.GetEnvironmentVariable("UNIBACKUP_TEST_SQL") ?? "",
        TrustServerCertificate = true
    };
    private readonly string _db = $"cp_test_{Guid.NewGuid():N}"[..20];

    private bool Enabled => !string.IsNullOrWhiteSpace(_src.Server);

    public async Task InitializeAsync()
    {
        if (Enabled) await ExecAsync($"CREATE DATABASE [{_db}]; ALTER DATABASE [{_db}] SET RECOVERY FULL;");
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
            await ExecAsync($"IF DB_ID('{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]; END");
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_svc.BuildConnectionString(_src));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SqlFact]
    public async Task ListDatabases_IncludesTestDb_ExcludesTempdb()
    {
        var dbs = await _svc.ListDatabasesAsync(_src, default);

        Assert.True(dbs.IsSuccess, dbs.Error);
        Assert.Contains(dbs.Value, d => d.Name == _db && d.RecoveryModel == "FULL" && !d.IsSystem);
        Assert.DoesNotContain(dbs.Value, d => d.Name == "tempdb");
        Assert.Contains(dbs.Value, d => d.Name == "master" && d.IsSystem);
    }

    [SqlFact]
    public async Task FreshDatabase_HasNoChain()
    {
        var chain = await _svc.GetChainStateAsync(_src, _db, default);

        Assert.True(chain.IsSuccess, chain.Error);
        Assert.Equal(new SqlChainState(false, false), chain.Value);
    }

    [SqlFact]
    public async Task FullThenLog_BackupVerify_AndChainStarts()
    {
        var folder = await _svc.ResolveStagingFolderAsync(_src, "itest", default);
        Assert.True(folder.IsSuccess, folder.Error);

        var full = Path.Combine(folder.Value, SqlBackupFileName.Format(_db, SqlBackupType.Full, DateTime.Now));
        var percents = new List<int>();
        var backup = await _svc.BackupAsync(_src, _db, SqlBackupType.Full, full, new SyncProgress<int>(percents.Add), default);

        Assert.True(backup.IsSuccess, backup.Error);
        Assert.True(File.Exists(full));
        Assert.NotEmpty(percents);
        Assert.True((await _svc.VerifyAsync(_src, full, default)).IsSuccess);
        var chain = (await _svc.GetChainStateAsync(_src, _db, default)).Value;
        Assert.True(chain.HasDifferentialBase && chain.HasLogChain);
        Assert.Equal(full, chain.LastFullFile, ignoreCase: true);

        var log = Path.Combine(folder.Value, SqlBackupFileName.Format(_db, SqlBackupType.Log, DateTime.Now));
        var logResult = await _svc.BackupAsync(_src, _db, SqlBackupType.Log, log, null, default);
        Assert.True(logResult.IsSuccess, logResult.Error);

        File.Delete(full);
        File.Delete(log);
    }

    [SqlFact]
    public async Task Backup_OfMissingDatabase_FailsWithSqlErrorNumber()
    {
        var folder = (await _svc.ResolveStagingFolderAsync(_src, "itest", default)).Value;

        var result = await _svc.BackupAsync(_src, "cp_does_not_exist", SqlBackupType.Full, Path.Combine(folder, "x.bak"), null, default);

        Assert.True(result.IsFailure);
        Assert.Contains("[3013]", result.Error); // BACKUP DATABASE is terminating abnormally
    }

    [SqlFact]
    public async Task Pipeline_LogRequestOnFreshDb_TakesFull_AndUploadsIntoDbTypeFolder()
    {
        var mock = new MockFailingStorageProvider { FailuresBeforeSuccess = 0 };
        var pipeline = NewPipeline(mock);
        var job = new BackupJob
        {
            Name = "itest", Kind = "sqlserver", Destinations = ["mock"], CloudFolder = "itest",
            Sql = new SqlServerSource { Server = _src.Server, TrustServerCertificate = true, AllUserDatabases = false, Databases = [_db] }
        };

        var history = await pipeline.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Log);

        Assert.Equal("SUCCESS", history.Status);
        var run = Assert.Single(history.SqlDatabases);
        Assert.Equal(SqlBackupType.Full, run.BackupType);
        Assert.Equal(1, mock.AttemptCount);
        Assert.Equal($"itest/{SqlBackupFileName.RemoteSubfolder(_db, SqlBackupType.Full)}", mock.LastFolder);
    }

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"UniBackup_SqlTest_{Guid.NewGuid():N}");

    private BackupPipeline NewPipeline(IStorageProvider storage) =>
        new(new HighSpeedZipArchiveService(), [storage], new EmailNotificationService(),
            new WebhookNotificationService(), new ConfigurationService(new CredentialProtector(), _dataDir), _svc);

    private BackupJob SqlJob() => new()
    {
        Id = "chain_" + _db, Name = "itest", Kind = "sqlserver", Destinations = ["mock"], CloudFolder = "itest",
        Sql = new SqlServerSource { Server = _src.Server, AllUserDatabases = false, Databases = [_db] }
    };

    [SqlFact]
    public async Task Diff_IsTaken_OnlyWhenTheFullBaseReachedTheCloud()
    {
        var job = SqlJob();
        var ok = NewPipeline(new MockFailingStorageProvider { FailuresBeforeSuccess = 0 });

        await ok.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Full);
        var diff = await ok.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Diff);
        Assert.Equal(SqlBackupType.Diff, Assert.Single(diff.SqlDatabases).BackupType);

        // Another tool takes a normal FULL: SQL Server now bases DIFFs on a file the cloud doesn't have.
        var folder = (await _svc.ResolveStagingFolderAsync(_src, "foreign", default)).Value;
        var foreign = Path.Combine(folder, "foreign_full.bak");
        Assert.True((await _svc.BackupAsync(_src, _db, SqlBackupType.Full, foreign, null, default)).IsSuccess);
        File.Delete(foreign);

        var afterForeign = await ok.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Diff);
        Assert.Equal(SqlBackupType.Full, Assert.Single(afterForeign.SqlDatabases).BackupType);

        // Our FULL whose upload fails must not become the base either.
        var failing = NewPipeline(new MockFailingStorageProvider { FailuresBeforeSuccess = 99 });
        var failedFull = await failing.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Full);
        Assert.False(Assert.Single(failedFull.SqlDatabases).Uploaded);

        var afterFailed = await ok.ExecuteJobAsync(new AppConfig(), job, sqlType: SqlBackupType.Diff);
        Assert.Equal(SqlBackupType.Full, Assert.Single(afterFailed.SqlDatabases).BackupType);
    }

    [Theory]
    [InlineData(@"localhost\SQL2022", true)]
    [InlineData(".", true)]
    [InlineData(@"(local)\X", true)]
    [InlineData("tcp:127.0.0.1,1433", true)]
    [InlineData(@"db01.corp.local\SQL2022", false)]
    public void IsLocalServer(string server, bool expected)
    {
        Assert.Equal(expected, SqlServerBackupService.IsLocalServer(server));
    }

    [SqlFact]
    public async Task CheckDb_OnHealthyDatabase_Succeeds()
    {
        var result = await _svc.CheckDbAsync(_src, _db, default);
        Assert.True(result.IsSuccess, result.Error);
    }
}
