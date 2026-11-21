using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using Xunit;

namespace UniBackup.Tests;

public class RestoreDrillServiceTests
{
    private class FakeEmptyStorageProvider : IStorageProvider
    {
        public string Name => "s3";
        public bool IsEnabled(AppConfig config) => true;

        public Task<Result<DestinationRunResult>> UploadAsync(
            AppConfig config, BackupJob job, string localFilePath, string? targetFolderOverride = null,
            IProgress<(long uploadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(Result<DestinationRunResult>.Failure("Not implemented"));

        public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(AppConfig config, string? folderPrefix = null, CancellationToken ct = default) =>
            Task.FromResult(Result<List<CloudArchiveItem>>.Success(new List<CloudArchiveItem>())); // Empty archive list

        public Task<Result> DownloadAsync(AppConfig config, string keyOrId, string destinationFilePath,
            IProgress<(long downloadedBytes, long totalBytes)>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<int>> CleanupRetentionAsync(AppConfig config, string targetFolder, int retentionDays, CancellationToken ct = default) =>
            Task.FromResult(Result<int>.Success(0));

        public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task RunDrill_WhenNoBackupsFound_ReturnsFailureWithRto()
    {
        var crypto = new CredentialProtector();
        var sqlBackup = new SqlServerBackupService(crypto);
        var sqlRestore = new SqlServerRestoreService(crypto);
        var orchestrator = new SqlRestoreOrchestrator([new FakeEmptyStorageProvider()], sqlRestore);
        var drillService = new RestoreDrillService(orchestrator, sqlBackup);

        var config = new AppConfig();
        var job = new BackupJob
        {
            Id = "drill-test",
            Name = "Drill Job",
            Kind = "sqlserver",
            Destinations = ["s3"],
            Sql = new SqlServerSource
            {
                Server = "localhost",
                Databases = ["TestDb"]
            }
        };

        var result = await drillService.RunDrillAsync(config, job, "TestDb");

        Assert.False(result.Success);
        Assert.False(result.CheckDbPassed);
        Assert.Equal("TestDb", result.Database);
        Assert.Equal("ub_drill_TestDb", result.DrillDatabaseName);
        Assert.NotNull(result.ErrorMessage);
        Assert.True(result.RtoSeconds >= 0);
    }

    [Fact]
    public async Task RunDrill_MissingSqlConfig_ReturnsFailure()
    {
        var crypto = new CredentialProtector();
        var sqlBackup = new SqlServerBackupService(crypto);
        var sqlRestore = new SqlServerRestoreService(crypto);
        var orchestrator = new SqlRestoreOrchestrator([new FakeEmptyStorageProvider()], sqlRestore);
        var drillService = new RestoreDrillService(orchestrator, sqlBackup);

        var config = new AppConfig();
        var job = new BackupJob
        {
            Id = "drill-nosql",
            Name = "No SQL Job",
            Kind = "folder"
        };

        var result = await drillService.RunDrillAsync(config, job, "TestDb");

        Assert.False(result.Success);
        Assert.Contains("SQL Server backup job", result.ErrorMessage);
    }
}
