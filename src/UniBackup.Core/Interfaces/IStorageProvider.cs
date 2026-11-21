using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Core.Interfaces;

public interface IStorageProvider
{
    string Name { get; }
    bool IsEnabled(AppConfig config);

    Task<Result<DestinationRunResult>> UploadAsync(
        AppConfig config,
        BackupJob job,
        string localFilePath,
        string? targetFolderOverride = null,
        IProgress<(long uploadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default);

    Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default);

    Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default);

    Task<Result> DeleteAsync(
        AppConfig config,
        string keyOrId,
        CancellationToken ct = default);

    Task<Result<int>> CleanupRetentionAsync(
        AppConfig config,
        string targetFolder,
        int retentionDays,
        CancellationToken ct = default);

    Task<Result> ValidateConnectionAsync(
        AppConfig config,
        CancellationToken ct = default);
}
