using System.Text.Json;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Storage;
using Xunit;

namespace UniBackup.Tests;

public class SqliteHistoryRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteHistoryRepository _repo;

    public SqliteHistoryRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_test_sqlite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "UniBackup.db");
        _repo = new SqliteHistoryRepository(_dbPath);
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
    public async Task InitializeAsync_CreatesDatabaseFileAndTables()
    {
        await _repo.InitializeAsync();
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task RecordRun_And_GetRuns_RoundTrips()
    {
        await _repo.InitializeAsync();

        var record = new RunHistoryRecord
        {
            RunId = "run_123",
            JobId = "job_1",
            JobName = "Daily Production SQL",
            Timestamp = DateTime.UtcNow,
            DurationSeconds = 300,
            Status = "SUCCESS",
            Trigger = "Schedule",
            ArchiveSizeBytes = 1024 * 1024 * 50,
            Kind = "sqlserver",
            SqlDatabases =
            [
                new SqlDatabaseRunResult
                {
                    Database = "OrdersDB",
                    BackupType = SqlBackupType.Full,
                    FileName = "OrdersDB_FULL_20261121_020000.bak",
                    SizeBytes = 1024 * 1024 * 50,
                    Status = "SUCCESS",
                    Uploaded = true
                }
            ],
            Destinations =
            [
                new DestinationRunResult
                {
                    Destination = "s3",
                    Status = "SUCCESS",
                    BytesUploaded = 1024 * 1024 * 50,
                    DurationSeconds = 12.5,
                    TargetLocation = "s3://backups/OrdersDB/FULL/OrdersDB_FULL_20261121_020000.bak"
                }
            ]
        };

        await _repo.RecordRunAsync(record);

        var retrieved = await _repo.GetRunsAsync(limit: 10);
        Assert.Single(retrieved);
        var r = retrieved[0];
        Assert.Equal("run_123", r.RunId);
        Assert.Equal("job_1", r.JobId);
        Assert.Equal("SUCCESS", r.Status);
        Assert.Equal("sqlserver", r.Kind);
        Assert.Single(r.SqlDatabases);
        Assert.Equal("OrdersDB", r.SqlDatabases[0].Database);
        Assert.Equal(SqlBackupType.Full, r.SqlDatabases[0].BackupType);
        Assert.True(r.SqlDatabases[0].Uploaded);
        Assert.Single(r.Destinations);
        Assert.Equal("s3", r.Destinations[0].Destination);
    }

    [Fact]
    public async Task SaveAndGet_JobRunState_RoundTrips()
    {
        await _repo.InitializeAsync();

        var state = new JobRunState
        {
            JobId = "job_sql_1",
            LastFullTime = DateTimeOffset.UtcNow.AddHours(-4),
            LastFullFile = "DB_FULL_20261121_020000.bak",
            LastDiffTime = DateTimeOffset.UtcNow.AddHours(-1),
            LastRunStatus = "SUCCESS",
            RetryCount = 0,
            UploadedBases = new Dictionary<string, string>
            {
                ["Shop"] = "Shop_FULL_20261121_020000.bak"
            }
        };

        await _repo.SaveRunStateAsync(state);

        var loaded = await _repo.GetRunStateAsync("job_sql_1");
        Assert.NotNull(loaded);
        Assert.Equal("job_sql_1", loaded.JobId);
        Assert.Equal("DB_FULL_20261121_020000.bak", loaded.LastFullFile);
        Assert.Equal("Shop_FULL_20261121_020000.bak", loaded.UploadedBases["Shop"]);
    }

    [Fact]
    public async Task ImportHistoryJson_ImportsExistingRecords_AndRenamesFile()
    {
        await _repo.InitializeAsync();

        var jsonPath = Path.Combine(_tempDir, "history.json");
        var records = new List<RunHistoryRecord>
        {
            new()
            {
                RunId = "legacy_1",
                JobId = "legacy_job",
                JobName = "Web Backup",
                Timestamp = DateTime.UtcNow.AddDays(-2),
                Status = "SUCCESS",
                ArchiveSizeBytes = 1000
            }
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(records));

        var importedCount = await _repo.ImportHistoryJsonAsync(jsonPath);

        Assert.Equal(1, importedCount);
        Assert.False(File.Exists(jsonPath));
        Assert.True(File.Exists(Path.Combine(_tempDir, "history.imported.json")));

        var runs = await _repo.GetRunsAsync();
        Assert.Single(runs);
        Assert.Equal("legacy_1", runs[0].RunId);
    }

    [Fact]
    public async Task PruneHistory_DeletesRecordsOlderThanRetentionDays()
    {
        await _repo.InitializeAsync();

        var oldRecord = new RunHistoryRecord
        {
            RunId = "old_run",
            JobId = "job1",
            Timestamp = DateTime.UtcNow.AddDays(-100),
            Status = "SUCCESS"
        };
        var newRecord = new RunHistoryRecord
        {
            RunId = "new_run",
            JobId = "job1",
            Timestamp = DateTime.UtcNow.AddDays(-10),
            Status = "SUCCESS"
        };

        await _repo.RecordRunAsync(oldRecord);
        await _repo.RecordRunAsync(newRecord);

        var prunedCount = await _repo.PruneHistoryAsync(retentionDays: 30);
        Assert.Equal(1, prunedCount);

        var remaining = await _repo.GetRunsAsync();
        Assert.Single(remaining);
        Assert.Equal("new_run", remaining[0].RunId);
    }
}
