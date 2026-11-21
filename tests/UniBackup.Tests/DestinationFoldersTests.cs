using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class DestinationFoldersTests
{
    [Fact]
    public void JobFolderWins()
    {
        var cfg = new AppConfig();
        cfg.Aws.S3Prefix = "cfg/";
        Assert.Equal("jobs/web", DestinationFolders.Resolve("s3", cfg, new BackupJob { CloudFolder = "jobs/web/" }));
    }

    [Fact]
    public void ConfigFolderUsedWhenJobBlank()
    {
        var cfg = new AppConfig();
        cfg.Aws.S3Prefix = "cfg/";
        Assert.Equal("cfg", DestinationFolders.Resolve("s3", cfg, new BackupJob()));
    }

    [Fact]
    public void BlankEverything_FallsBackToServerDefault_NeverBucketRoot()
    {
        var cfg = new AppConfig();
        cfg.Aws.S3Prefix = "";
        cfg.GDrive.TargetFolder = "";

        Assert.Equal(ServerIdentity.Current.DefaultS3Prefix.TrimEnd('/'), DestinationFolders.Resolve("s3", cfg, new BackupJob()));
        Assert.Equal(ServerIdentity.Current.DefaultGDriveFolder.TrimEnd('/'), DestinationFolders.Resolve("gdrive", cfg, new BackupJob()));
    }
}
