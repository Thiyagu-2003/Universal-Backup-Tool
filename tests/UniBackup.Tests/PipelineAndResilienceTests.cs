using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using Polly;
using Polly.Retry;
using Xunit;

namespace UniBackup.Tests;

public class MockFailingStorageProvider : IStorageProvider
{
    public string Name => "mock";
    public int AttemptCount { get; private set; }
    public int FailuresBeforeSuccess { get; set; } = 2;
    public string? LastFolder { get; private set; }

    public bool IsEnabled(AppConfig config) => true;

    public Task<Result<DestinationRunResult>> UploadAsync(
        AppConfig config,
        BackupJob job,
        string localFilePath,
        string? targetFolderOverride = null,
        IProgress<(long uploadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        LastFolder = targetFolderOverride;
        AttemptCount++;
        if (AttemptCount <= FailuresBeforeSuccess)
        {
            return Task.FromResult(Result<DestinationRunResult>.Failure($"Simulated network timeout (attempt {AttemptCount})"));
        }

        return Task.FromResult(Result<DestinationRunResult>.Success(new DestinationRunResult
        {
            Destination = Name,
            Status = "SUCCESS",
            BytesUploaded = 1024,
            DurationSeconds = 1.0,
            TargetLocation = "mock://bucket/backup.zip"
        }));
    }

    public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(AppConfig config, string? folderPrefix = null, CancellationToken ct = default) =>
        Task.FromResult(Result<List<CloudArchiveItem>>.Success([]));

    public Task<Result> DownloadAsync(AppConfig config, string keyOrId, string destinationFilePath, IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    public Task<Result<int>> CleanupRetentionAsync(AppConfig config, string targetFolder, int retentionDays, CancellationToken ct = default) =>
        Task.FromResult(Result<int>.Success(0));

    public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}

public class PipelineAndResilienceTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConfigurationService _configService;
    private readonly HighSpeedZipArchiveService _archiveService = new();
    private readonly EmailNotificationService _emailService = new();
    private readonly WebhookNotificationService _webhookService = new();
    private readonly CredentialProtector _crypto = new();

    public PipelineAndResilienceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CloudPacker_PipelineTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _configService = new ConfigurationService(_crypto, _testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public async Task Test_Missing_Source_Folder_Fails_Gracefully()
    {
        var mockStorage = new MockFailingStorageProvider();
        var pipeline = new BackupPipeline(_archiveService, [mockStorage], _emailService, _webhookService, _configService, new SqlServerBackupService(_crypto));

        var config = new AppConfig();
        config.Backup.TempFolder = _testDir;

        var missingJob = new BackupJob
        {
            Id = "missing_01",
            Name = "MissingDirectoryJob",
            SourceFolder = Path.Combine(_testDir, "NonExistentFolder_12345"),
            Destinations = ["mock"]
        };

        var history = await pipeline.ExecuteJobAsync(config, missingJob);

        Assert.Equal("FAILED", history.Status);
        Assert.NotNull(history.ErrorMessage);
        Assert.Contains("does not exist", history.ErrorMessage);
    }

    private sealed class CountingNotifier : INotificationService
    {
        public int RunReports { get; private set; }
        public Task<Result> SendRunReportAsync(AppConfig config, BackupJob job, RunHistoryRecord history, CancellationToken ct = default)
        {
            RunReports++;
            return Task.FromResult(Result.Success());
        }
        public Task<Result> SendBatchReportAsync(AppConfig config, List<RunHistoryRecord> histories, TimeSpan totalDuration, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
        public Task<Result> SendTestNotificationAsync(AppConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task SecondRunOfSameJob_IsSkippedWhileFirstHoldsTheLock()
    {
        var notifier = new CountingNotifier();
        var pipeline = new BackupPipeline(_archiveService, [new MockFailingStorageProvider()], notifier, _webhookService, _configService, new SqlServerBackupService(_crypto));
        var job = new BackupJob { Id = "locked_01", SourceFolder = _testDir, Destinations = ["mock"] };
        var lockPath = pipeline.JobLockPath(job.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) // "another process" is running it
        {
            var history = await pipeline.ExecuteJobAsync(new AppConfig(), job);
            Assert.Equal("FAILED", history.Status);
            Assert.Contains("already running", history.ErrorMessage);
        }

        Assert.Equal(0, notifier.RunReports); // skipped runs send no alerts and write no history
    }

    [Fact]
    public async Task EarlyFailure_IsFinalisedExactlyOnce()
    {
        var notifier = new CountingNotifier();
        var pipeline = new BackupPipeline(_archiveService, [new MockFailingStorageProvider()], notifier, _webhookService, _configService, new SqlServerBackupService(_crypto));
        var config = new AppConfig();
        config.Backup.TempFolder = _testDir;

        await pipeline.ExecuteJobAsync(config, new BackupJob { Id = "once_01", SourceFolder = Path.Combine(_testDir, "nope"), Destinations = ["mock"] });

        Assert.Equal(1, notifier.RunReports);
    }

    [Fact]
    public async Task Test_Polly_Retry_Recovers_From_Transient_Network_Failures()
    {
        var mockStorage = new MockFailingStorageProvider { FailuresBeforeSuccess = 2 };

        // Test Polly retry pipeline
        var retryPipeline = new ResiliencePipelineBuilder<Result<DestinationRunResult>>()
            .AddRetry(new RetryStrategyOptions<Result<DestinationRunResult>>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder<Result<DestinationRunResult>>()
                    .HandleResult(r => r.IsFailure)
            })
            .Build();

        var job = new BackupJob { Name = "TestJob" };
        var cfg = new AppConfig();

        var result = await retryPipeline.ExecuteAsync(async _ =>
        {
            return await mockStorage.UploadAsync(cfg, job, "dummy.zip");
        });

        // Must succeed on the 3rd attempt
        Assert.True(result.IsSuccess);
        Assert.Equal("SUCCESS", result.Value.Status);
        Assert.Equal(3, mockStorage.AttemptCount);
    }
}
