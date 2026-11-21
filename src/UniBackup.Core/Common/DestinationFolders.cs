using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

/// The single place a destination's base folder is resolved. Never empty, no trailing '/'.
public static class DestinationFolders
{
    public static string Resolve(string provider, AppConfig config, BackupJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.CloudFolder)) return job.CloudFolder.TrimEnd('/');

        var isS3 = provider.Equals("s3", StringComparison.OrdinalIgnoreCase);
        var configured = isS3 ? config.Aws.S3Prefix : config.GDrive.TargetFolder;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        return (isS3 ? ServerIdentity.Current.DefaultS3Prefix : ServerIdentity.Current.DefaultGDriveFolder).TrimEnd('/');
    }
}
