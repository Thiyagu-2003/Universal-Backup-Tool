using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

/// The single place a destination's base folder is resolved. Never empty, no trailing '/'.
public static class DestinationFolders
{
    public static string Resolve(string provider, AppConfig config, BackupJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.CloudFolder)) return job.CloudFolder.TrimEnd('/');

        var isS3 = provider.Equals("s3", StringComparison.OrdinalIgnoreCase);
        if (provider.Equals("azure", StringComparison.OrdinalIgnoreCase)) return "backups";
        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.Local.TargetPath) ? @"C:\UniBackup\Backups" : config.Local.TargetPath.TrimEnd('\\', '/');
        if (provider.Equals("ftp", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.Ftp.RemoteFolder) ? "/backups" : config.Ftp.RemoteFolder.TrimEnd('/');
        if (provider.Equals("sftp", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.Sftp.RemoteFolder) ? "/backups" : config.Sftp.RemoteFolder.TrimEnd('/');
        if (provider.Equals("webdav", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.WebDav.RemoteFolder) ? "/backups" : config.WebDav.RemoteFolder.TrimEnd('/');
        if (provider.Equals("dropbox", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.Dropbox.RemoteFolder) ? "/UniBackup" : config.Dropbox.RemoteFolder.TrimEnd('/');
        if (provider.Equals("onedrive", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.OneDrive.RemoteFolder) ? "/Backups" : config.OneDrive.RemoteFolder.TrimEnd('/');
        if (provider.Equals("box", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(config.Box.FolderId) ? "0" : config.Box.FolderId.Trim();

        var configured = isS3 ? config.Aws.S3Prefix : config.GDrive.TargetFolder;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        return (isS3 ? ServerIdentity.Current.DefaultS3Prefix : ServerIdentity.Current.DefaultGDriveFolder).TrimEnd('/');
    }
}
