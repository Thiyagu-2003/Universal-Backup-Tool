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
using Xunit;

namespace UniBackup.Tests;

public class DestinationRoutingTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConfigurationService _configService;

    public DestinationRoutingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"UniBackup_RoutingTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _configService = new ConfigurationService(new CredentialProtector(), _testDir);
    }

    [Fact]
    public void DestinationFolders_ResolvesCustomProviders()
    {
        var config = new AppConfig
        {
            Local = new LocalStorageSettings { TargetPath = @"D:\MyBackups" },
            Ftp = new FtpSettings { RemoteFolder = "/ftp/site" },
            Sftp = new SftpSettings { RemoteFolder = "/sftp/site" },
            WebDav = new WebDavSettings { RemoteFolder = "/dav/site" }
        };
        var job = new BackupJob();

        Assert.Equal(@"D:\MyBackups", DestinationFolders.Resolve("local", config, job));
        Assert.Equal("/ftp/site", DestinationFolders.Resolve("ftp", config, job));
        Assert.Equal("/sftp/site", DestinationFolders.Resolve("sftp", config, job));
        Assert.Equal("/dav/site", DestinationFolders.Resolve("webdav", config, job));
    }

    [Fact]
    public void S3Compatible_Preset_CanBeConfigured()
    {
        var config = new AppConfig();
        config.Aws.ServiceUrl = "https://s3.wasabisys.com";
        config.Aws.ForcePathStyle = true;
        config.Aws.Preset = "wasabi";

        Assert.Equal("https://s3.wasabisys.com", config.Aws.ServiceUrl);
        Assert.True(config.Aws.ForcePathStyle);
        Assert.Equal("wasabi", config.Aws.Preset);
    }

    [Fact]
    public void DestinationRouting_Filters_By_BackupType()
    {
        var job = new BackupJob
        {
            Destinations = ["s3", "local"],
            DestinationRouting =
            [
                new DestinationRoutingRule
                {
                    Destination = "s3",
                    BackupTypes = [SqlBackupType.Full, SqlBackupType.Diff]
                },
                new DestinationRoutingRule
                {
                    Destination = "local",
                    BackupTypes = [SqlBackupType.Log]
                }
            ]
        };

        var config = new AppConfig
        {
            Aws = new AwsSettings { Enabled = true },
            Local = new LocalStorageSettings { Enabled = true }
        };

        var s3 = new AwsS3StorageProvider();
        var local = new LocalStorageProvider();
        var pipeline = new BackupPipeline(
            new HighSpeedZipArchiveService(),
            [s3, local],
            new EmailNotificationService(),
            new WebhookNotificationService(),
            _configService,
            new SqlServerBackupService(new CredentialProtector()));

        // Check using reflection on private ActiveProviders
        var method = typeof(BackupPipeline).GetMethod("ActiveProviders",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var fullProviders = (List<IStorageProvider>)method.Invoke(pipeline, [config, job, SqlBackupType.Full])!;
        Assert.Single(fullProviders);
        Assert.Equal("s3", fullProviders[0].Name);

        var logProviders = (List<IStorageProvider>)method.Invoke(pipeline, [config, job, SqlBackupType.Log])!;
        Assert.Single(logProviders);
        Assert.Equal("local", logProviders[0].Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
