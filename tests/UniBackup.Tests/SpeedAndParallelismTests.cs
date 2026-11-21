using System.Diagnostics;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using Xunit;

namespace UniBackup.Tests;

public class DelayedTestStorageProvider : IStorageProvider
{
    private readonly int _delayMs;
    public string Name { get; }
    public int UploadCount { get; private set; }
    public DateTime? StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }

    public DelayedTestStorageProvider(string name, int delayMs)
    {
        Name = name;
        _delayMs = delayMs;
    }

    public bool IsEnabled(AppConfig config) => true;

    public async Task<Result<DestinationRunResult>> UploadAsync(
        AppConfig config,
        BackupJob job,
        string localFilePath,
        string? targetFolderOverride = null,
        IProgress<(long uploadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        StartTime = DateTime.UtcNow;
        await Task.Delay(_delayMs, ct);
        EndTime = DateTime.UtcNow;
        UploadCount++;
        return Result<DestinationRunResult>.Success(new DestinationRunResult
        {
            Destination = Name,
            Status = "SUCCESS",
            BytesUploaded = 1024,
            DurationSeconds = _delayMs / 1000.0,
            TargetLocation = $"{Name}://backups/archive.zip"
        });
    }

    public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(AppConfig config, string? folderPrefix = null, CancellationToken ct = default) =>
        Task.FromResult(Result<List<CloudArchiveItem>>.Success([]));

    public Task<Result> DownloadAsync(AppConfig config, string keyOrId, string destinationFilePath, IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result<int>> CleanupRetentionAsync(AppConfig config, string targetFolder, int retentionDays, CancellationToken ct = default) =>
        Task.FromResult(Result<int>.Success(0));

    public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}

public class SpeedAndParallelismTests
{
    [Fact]
    public async Task ParallelUploads_ExecuteConcurrently_AcrossDestinations()
    {
        var prov1 = new DelayedTestStorageProvider("s3_mock", 200);
        var prov2 = new DelayedTestStorageProvider("gdrive_mock", 200);

        var tempDir = Path.Combine(Path.GetTempPath(), $"UniBackup_SpeedTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcDir = Path.Combine(tempDir, "source");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "sample.txt"), "Speed test content");

            var configService = new ConfigurationService(new CredentialProtector(), tempDir);
            var pipeline = new BackupPipeline(
                new HighSpeedZipArchiveService(),
                [prov1, prov2],
                new EmailNotificationService(),
                new WebhookNotificationService(),
                configService,
                new SqlServerBackupService(new CredentialProtector()));

            var job = new BackupJob
            {
                Id = "speed_job_01",
                Name = "SpeedJob",
                SourceFolder = srcDir,
                Destinations = ["s3_mock", "gdrive_mock"]
            };

            var cfg = new AppConfig();
            cfg.Backup.TempFolder = tempDir;

            var sw = Stopwatch.StartNew();
            var history = await pipeline.ExecuteJobAsync(cfg, job);
            sw.Stop();

            Assert.Equal("SUCCESS", history.Status);
            Assert.Equal(1, prov1.UploadCount);
            Assert.Equal(1, prov2.UploadCount);
            Assert.Equal(2, history.Destinations.Count);

            Assert.NotNull(prov1.StartTime);
            Assert.NotNull(prov1.EndTime);
            Assert.NotNull(prov2.StartTime);
            Assert.NotNull(prov2.EndTime);

            // Both providers must execute concurrently (start of one before end of the other)
            Assert.True(prov2.StartTime < prov1.EndTime && prov1.StartTime < prov2.EndTime,
                $"Expected concurrent execution: Prov1 ({prov1.StartTime:HH:mm:ss.fff} - {prov1.EndTime:HH:mm:ss.fff}), Prov2 ({prov2.StartTime:HH:mm:ss.fff} - {prov2.EndTime:HH:mm:ss.fff})");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SqlBackupText_BackupToUrl_GeneratesValidSyntax()
    {
        var options = new SqlServerSource
        {
            Checksum = true,
            NativeCompression = true,
            CopyOnly = false
        };

        var sql = SqlBackupText.BackupToUrl(SqlBackupType.Full, options, true, ["s3://my-endpoint.com/bucket/full.bak"]);

        Assert.Equal("BACKUP DATABASE @db TO URL = @url0 WITH INIT, CHECKSUM, COMPRESSION, STATS = 5;", sql);
    }

    [Fact]
    public void SqlBackupText_CreateS3Credential_GeneratesValidSyntax()
    {
        var sql = SqlBackupText.CreateS3Credential("s3://my-endpoint.com/bucket", "AKIAACCESSKEY", "SecretPassword'123");

        Assert.Contains("CREATE CREDENTIAL [s3://my-endpoint.com/bucket]", sql);
        Assert.Contains("IDENTITY = 'S3 Access Key'", sql);
        Assert.Contains("SECRET = 'AKIAACCESSKEY:SecretPassword''123'", sql);
    }
}
