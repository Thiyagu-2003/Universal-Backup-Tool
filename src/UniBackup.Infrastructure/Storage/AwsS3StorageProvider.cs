using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Infrastructure.Storage;

public class AwsS3StorageProvider : IStorageProvider
{
    public string Name => "s3";

    public bool IsEnabled(AppConfig config) => config.Aws.Enabled;

    private (AmazonS3Client Client, RegionEndpoint Region) CreateClient(AppConfig config)
    {
        var region = RegionEndpoint.GetBySystemName(config.Aws.Region);
        var s3Config = new AmazonS3Config
        {
            RegionEndpoint = region,
            Timeout = TimeSpan.FromSeconds(300),
            MaxErrorRetry = 3
        };

        if (!string.IsNullOrWhiteSpace(config.Aws.AccessKeyId) && !string.IsNullOrWhiteSpace(config.Aws.SecretAccessKey))
        {
            var credentials = new BasicAWSCredentials(config.Aws.AccessKeyId, config.Aws.SecretAccessKey);
            return (new AmazonS3Client(credentials, s3Config), region);
        }

        // Fall back to IAM Role / EC2 Instance Profile / AWS Credential Chain
        return (new AmazonS3Client(s3Config), region);
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

        if (!File.Exists(localFilePath))
            return Result<DestinationRunResult>.Failure($"Archive file does not exist: {localFilePath}");

        var fileInfo = new FileInfo(localFilePath);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                var prefix = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);

                if (!prefix.EndsWith('/')) prefix += "/";
                var key = $"{prefix}{fileInfo.Name}";
                result.TargetLocation = $"s3://{config.Aws.BucketName}/{key}";

                Log.Information("[AWS S3] Uploading {File} ({Size}) to {TargetLocation}...",
                    fileInfo.Name, FormattingUtils.FormatBytes(fileInfo.Length), result.TargetLocation);

                var transferUtilConfig = new TransferUtilityConfig
                {
                    MinSizeBeforePartUpload = config.Aws.MultipartChunkMb * 1024L * 1024L,
                    ConcurrentServiceRequests = 5
                };

                using var transferUtil = new TransferUtility(s3Client, transferUtilConfig);
                var uploadRequest = new TransferUtilityUploadRequest
                {
                    BucketName = config.Aws.BucketName,
                    Key = key,
                    FilePath = localFilePath,
                    StorageClass = S3StorageClass.FindValue(config.Aws.StorageClass),
                    PartSize = config.Aws.MultipartChunkMb * 1024L * 1024L
                };

                uploadRequest.UploadProgressEvent += (s, e) =>
                {
                    progress?.Report((e.TransferredBytes, e.TotalBytes));
                };

                await transferUtil.UploadAsync(uploadRequest, ct);

                sw.Stop();
                result.Status = "SUCCESS";
                result.BytesUploaded = fileInfo.Length;
                result.DurationSeconds = sw.Elapsed.TotalSeconds;

                Log.Information("[AWS S3] Upload completed successfully in {Duration} ({Speed})",
                    FormattingUtils.FormatDuration(sw.Elapsed),
                    FormattingUtils.FormatSpeed(fileInfo.Length, sw.Elapsed));

                return Result<DestinationRunResult>.Success(result);
            }
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Upload cancelled by user.";
            return Result<DestinationRunResult>.Failure(result.ErrorMessage);
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[AWS S3] Upload failed for {File}: {Error}", fileInfo.Name, ex.Message);
            return Result<DestinationRunResult>.Failure(ex.Message);
        }
    }

    public async Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var archives = new List<CloudArchiveItem>();
        try
        {
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                var prefix = folderPrefix ?? config.Aws.S3Prefix;
                var request = new ListObjectsV2Request
                {
                    BucketName = config.Aws.BucketName,
                    Prefix = prefix
                };

                ListObjectsV2Response response;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    response = await s3Client.ListObjectsV2Async(request, ct);

                    foreach (var obj in response.S3Objects)
                    {
                        if (obj.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            archives.Add(new CloudArchiveItem
                            {
                                Destination = Name,
                                KeyOrId = obj.Key,
                                DisplayName = Path.GetFileName(obj.Key),
                                CloudFolder = Path.GetDirectoryName(obj.Key)?.Replace('\\', '/') ?? "",
                                SizeBytes = obj.Size,
                                LastModified = obj.LastModified,
                                ServerIpOrTag = config.Aws.BucketName
                            });
                        }
                    }

                    request.ContinuationToken = response.NextContinuationToken;
                } while (response.IsTruncated);

                return Result<List<CloudArchiveItem>>.Success(archives.OrderByDescending(a => a.LastModified).ToList());
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AWS S3] Failed to list archives in {Bucket}: {Error}", config.Aws.BucketName, ex.Message);
            return Result<List<CloudArchiveItem>>.Failure($"S3 list error: {ex.Message}");
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
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                var dir = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using var transferUtil = new TransferUtility(s3Client);
                var downloadRequest = new TransferUtilityDownloadRequest
                {
                    BucketName = config.Aws.BucketName,
                    Key = keyOrId,
                    FilePath = destinationFilePath
                };

                downloadRequest.WriteObjectProgressEvent += (s, e) =>
                {
                    progress?.Report((e.TransferredBytes, e.TotalBytes));
                };

                await transferUtil.DownloadAsync(downloadRequest, ct);
                return Result.Success();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AWS S3] Download failed for key {Key}: {Error}", keyOrId, ex.Message);
            return Result.Failure($"Download failed: {ex.Message}");
        }
    }

    public async Task<Result<int>> CleanupRetentionAsync(
        AppConfig config,
        string targetFolder,
        int retentionDays,
        CancellationToken ct = default)
    {
        if (retentionDays <= 0) return Result<int>.Success(0);

        var deletedCount = 0;
        try
        {
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var prefix = targetFolder;
                if (!prefix.EndsWith('/')) prefix += "/";

                var request = new ListObjectsV2Request
                {
                    BucketName = config.Aws.BucketName,
                    Prefix = prefix
                };

                var toDelete = new List<KeyVersion>();
                ListObjectsV2Response response;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    response = await s3Client.ListObjectsV2Async(request, ct);

                    foreach (var obj in response.S3Objects)
                    {
                        if (obj.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && obj.LastModified < cutoff)
                        {
                            toDelete.Add(new KeyVersion { Key = obj.Key });
                        }
                    }

                    request.ContinuationToken = response.NextContinuationToken;
                } while (response.IsTruncated);

                if (toDelete.Count > 0)
                {
                    Log.Information("[AWS S3] Pruning {Count} archives older than {Days} days under {Prefix}",
                        toDelete.Count, retentionDays, prefix);

                    var deleteRequest = new DeleteObjectsRequest
                    {
                        BucketName = config.Aws.BucketName,
                        Objects = toDelete
                    };

                    var deleteResponse = await s3Client.DeleteObjectsAsync(deleteRequest, ct);
                    deletedCount = deleteResponse.DeletedObjects.Count;
                }

                return Result<int>.Success(deletedCount);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AWS S3] Retention cleanup failed: {Error}", ex.Message);
            return Result<int>.Failure($"S3 cleanup error: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        try
        {
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                await s3Client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = config.Aws.BucketName,
                    Key = keyOrId
                }, ct);
                return Result.Success();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AWS S3] Delete failed for {Key}: {Error}", keyOrId, ex.Message);
            return Result.Failure($"S3 delete error: {ex.Message}");
        }
    }

    public async Task<Result<int>> AbortMultipartUploadsOlderThanAsync(AppConfig config, TimeSpan maxAge, CancellationToken ct = default)
    {
        try
        {
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                var cutoff = DateTime.UtcNow - maxAge;
                var aborted = 0;
                var listReq = new ListMultipartUploadsRequest { BucketName = config.Aws.BucketName };
                do
                {
                    ct.ThrowIfCancellationRequested();
                    var resp = await s3Client.ListMultipartUploadsAsync(listReq, ct);
                    foreach (var u in resp.MultipartUploads)
                    {
                        if (u.Initiated < cutoff)
                        {
                            await s3Client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                            {
                                BucketName = config.Aws.BucketName,
                                Key = u.Key,
                                UploadId = u.UploadId
                            }, ct);
                            aborted++;
                        }
                    }
                    listReq.KeyMarker = resp.NextKeyMarker;
                    listReq.UploadIdMarker = resp.NextUploadIdMarker;
                } while (listReq.KeyMarker != null);

                return Result<int>.Success(aborted);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AWS S3] Aborting multipart uploads failed: {Error}", ex.Message);
            return Result<int>.Failure($"Abort multipart uploads error: {ex.Message}");
        }
    }

    public async Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        try
        {
            var (s3Client, _) = CreateClient(config);
            using (s3Client)
            {
                var listReq = new ListObjectsV2Request
                {
                    BucketName = config.Aws.BucketName,
                    MaxKeys = 1
                };
                await s3Client.ListObjectsV2Async(listReq, ct);

                // Probe test file creation and deletion
                var probeKey = $"probe_{Guid.NewGuid():N}.tmp";
                await s3Client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = config.Aws.BucketName,
                    Key = probeKey,
                    ContentBody = "probe"
                }, ct);

                await s3Client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = config.Aws.BucketName,
                    Key = probeKey
                }, ct);

                return Result.Success();
            }
        }
        catch (Exception ex)
        {
            return Result.Failure($"AWS S3 validation failed: {ex.Message}");
        }
    }
}
