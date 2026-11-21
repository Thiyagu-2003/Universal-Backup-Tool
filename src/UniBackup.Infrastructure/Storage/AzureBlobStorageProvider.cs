using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class AzureBlobStorageProvider : IStorageProvider
{
    public string Name => "azure";

    public bool IsEnabled(AppConfig config) => config.Azure.Enabled;

    private BlobContainerClient GetContainerClient(AppConfig config)
    {
        var connStr = config.Azure.ConnectionString;
        var containerName = string.IsNullOrWhiteSpace(config.Azure.ContainerName) ? "unibackup" : config.Azure.ContainerName.ToLowerInvariant();
        return new BlobContainerClient(connStr, containerName);
    }

    public async Task<Result<DestinationRunResult>> UploadAsync(
        AppConfig config,
        BackupJob job,
        string localFilePath,
        string? targetFolderOverride = null,
        IProgress<(long uploadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new DestinationRunResult
        {
            Destination = Name,
            Status = "FAILED"
        };

        var fileName = Path.GetFileName(localFilePath);
        var folder = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
        var blobKey = string.IsNullOrWhiteSpace(folder) ? fileName : $"{folder.Trim('/')}/{fileName}";

        var sw = Stopwatch.StartNew();
        try
        {
            var container = GetContainerClient(config);
            await container.CreateIfNotExistsAsync(cancellationToken: ct);

            var blob = container.GetBlobClient(blobKey);
            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            var uploadOptions = new BlobUploadOptions();
            if (Enum.TryParse<AccessTier>(config.Azure.AccessTier, true, out var tier))
            {
                uploadOptions.AccessTier = tier;
            }

            uploadOptions.ProgressHandler = new Progress<long>(uploaded =>
            {
                progress?.Report((uploaded, totalBytes));
            });

            await using var fs = File.OpenRead(localFilePath);
            await blob.UploadAsync(fs, uploadOptions, ct);

            sw.Stop();
            result.Status = "SUCCESS";
            result.BytesUploaded = totalBytes;
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.TargetLocation = blob.Uri.ToString();

            Log.Information("[Azure Blob] Uploaded {File} ({Bytes:N0} bytes) to {Uri} in {Duration:F1}s",
                fileName, totalBytes, blob.Uri, sw.Elapsed.TotalSeconds);

            return Result<DestinationRunResult>.Success(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[Azure Blob] Failed uploading {File} to blob {Key}", fileName, blobKey);
            return Result<DestinationRunResult>.Failure($"Azure Blob upload failed: {ex.Message}");
        }
    }

    public async Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var items = new List<CloudArchiveItem>();
        try
        {
            var container = GetContainerClient(config);
            if (!await container.ExistsAsync(ct))
            {
                return Result<List<CloudArchiveItem>>.Success(items);
            }

            var prefix = folderPrefix?.Trim('/') != null ? folderPrefix.Trim('/') + "/" : null;
            await foreach (var blobItem in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                items.Add(new CloudArchiveItem
                {
                    KeyOrId = blobItem.Name,
                    DisplayName = Path.GetFileName(blobItem.Name),
                    CloudFolder = Path.GetDirectoryName(blobItem.Name)?.Replace('\\', '/') ?? "",
                    LastModified = blobItem.Properties.LastModified?.UtcDateTime ?? DateTime.UtcNow,
                    SizeBytes = blobItem.Properties.ContentLength ?? 0,
                    Destination = Name
                });
            }

            return Result<List<CloudArchiveItem>>.Success(items.OrderByDescending(i => i.LastModified).ToList());
        }
        catch (Exception ex)
        {
            return Result<List<CloudArchiveItem>>.Failure($"Azure list archives failed: {ex.Message}");
        }
    }

    public async Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var container = GetContainerClient(config);
            var blob = container.GetBlobClient(keyOrId);

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await blob.DownloadToAsync(destinationFilePath, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Azure download failed: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        try
        {
            var container = GetContainerClient(config);
            var blob = container.GetBlobClient(keyOrId);
            await blob.DeleteIfExistsAsync(cancellationToken: ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Azure delete failed: {ex.Message}");
        }
    }

    public async Task<Result<int>> CleanupRetentionAsync(
        AppConfig config,
        string targetFolder,
        int retentionDays,
        CancellationToken ct = default)
    {
        var deleted = 0;
        if (retentionDays <= 0) return Result<int>.Success(0);

        try
        {
            var container = GetContainerClient(config);
            if (!await container.ExistsAsync(ct)) return Result<int>.Success(0);

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var prefix = targetFolder?.Trim('/') != null ? targetFolder.Trim('/') + "/" : null;

            await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
            {
                if (blob.Properties.LastModified.HasValue && blob.Properties.LastModified.Value.UtcDateTime < cutoff)
                {
                    await container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: ct);
                    deleted++;
                }
            }

            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            return Result<int>.Failure($"Azure retention cleanup failed: {ex.Message}");
        }
    }

    public async Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.Azure.ConnectionString))
            {
                return Result.Failure("Azure connection string is empty.");
            }

            var container = GetContainerClient(config);
            await container.ExistsAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Azure connection test failed: {ex.Message}");
        }
    }
}
