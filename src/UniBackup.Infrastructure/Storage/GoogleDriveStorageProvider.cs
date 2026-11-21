using System.Text.Json;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Serilog;

namespace UniBackup.Infrastructure.Storage;

public class GoogleDriveStorageProvider : IStorageProvider
{
    public string Name => "gdrive";

    public const string CredentialsFileName = "gdrive_credentials.json";
    public const string TokenFileName = "gdrive_token.json";

    public bool IsEnabled(AppConfig config) => config.GDrive.Enabled;

    private async Task<UserCredential?> GetUserCredentialAsync(CancellationToken ct)
    {
        var dataDir = AppPaths.ResolveDataDirectory();
        var credPath = Path.Combine(dataDir, CredentialsFileName);
        if (!File.Exists(credPath))
        {
            credPath = Path.Combine(AppPaths.AppBaseDirectory, CredentialsFileName);
        }

        if (!File.Exists(credPath))
        {
            Log.Warning("Google Drive credentials file not found: {Path}", credPath);
            return null;
        }

        await using var stream = new FileStream(credPath, FileMode.Open, FileAccess.Read);
        var tokenDir = Path.Combine(dataDir, "gdrive_token_store");
        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            [DriveService.Scope.Drive],
            "user",
            ct,
            new FileDataStore(tokenDir, true));
    }

    private async Task<DriveService?> CreateDriveServiceAsync(CancellationToken ct)
    {
        var credential = await GetUserCredentialAsync(ct);
        if (credential == null) return null;

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "UniBackup"
        });
    }

    private async Task<string> EnsureFolderPathAsync(DriveService service, string folderPath, CancellationToken ct)
    {
        var parts = folderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var parentId = "root";

        foreach (var part in parts)
        {
            var query = $"mimeType = 'application/vnd.google-apps.folder' and name = '{part}' and '{parentId}' in parents and trashed = false";
            var listReq = service.Files.List();
            listReq.Q = query;
            listReq.Fields = "files(id, name)";

            var result = await listReq.ExecuteAsync(ct);
            var folder = result.Files.FirstOrDefault();

            if (folder == null)
            {
                var newFolder = new Google.Apis.Drive.v3.Data.File
                {
                    Name = part,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = [parentId]
                };
                var createReq = service.Files.Create(newFolder);
                createReq.Fields = "id";
                var created = await createReq.ExecuteAsync(ct);
                parentId = created.Id;
            }
            else
            {
                parentId = folder.Id;
            }
        }

        return parentId;
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
            using var service = await CreateDriveServiceAsync(ct);
            if (service == null)
                return Result<DestinationRunResult>.Failure("Google Drive authentication is missing or invalid.");

            var folderPath = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);

            result.TargetLocation = $"gdrive://{folderPath}/{fileInfo.Name}";
            Log.Information("[Google Drive] Uploading {File} ({Size}) to {TargetLocation}...",
                fileInfo.Name, FormattingUtils.FormatBytes(fileInfo.Length), result.TargetLocation);

            var parentFolderId = await EnsureFolderPathAsync(service, folderPath, ct);

            var fileMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = fileInfo.Name,
                Parents = [parentFolderId]
            };

            await using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var uploadRequest = service.Files.Create(fileMetadata, stream, "application/zip");
            uploadRequest.ChunkSize = ResumableUpload.MinimumChunkSize * 4; // 1 MB chunk

            uploadRequest.ProgressChanged += (p) =>
            {
                if (p.Status == UploadStatus.Uploading)
                {
                    progress?.Report((p.BytesSent, fileInfo.Length));
                }
            };

            var uploadProgress = await uploadRequest.UploadAsync(ct);
            if (uploadProgress.Status != UploadStatus.Completed)
            {
                var err = uploadProgress.Exception?.Message ?? "Upload failed without exception";
                result.ErrorMessage = err;
                return Result<DestinationRunResult>.Failure(err);
            }

            sw.Stop();
            result.Status = "SUCCESS";
            result.BytesUploaded = fileInfo.Length;
            result.DurationSeconds = sw.Elapsed.TotalSeconds;

            Log.Information("[Google Drive] Upload completed successfully in {Duration}",
                FormattingUtils.FormatDuration(sw.Elapsed));

            return Result<DestinationRunResult>.Success(result);
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
            Log.Error(ex, "[Google Drive] Upload failed for {File}: {Error}", fileInfo.Name, ex.Message);
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
            using var service = await CreateDriveServiceAsync(ct);
            if (service == null)
                return Result<List<CloudArchiveItem>>.Failure("Google Drive authentication is missing or invalid.");

            var folderPath = folderPrefix ?? config.GDrive.TargetFolder;
            var parentId = await EnsureFolderPathAsync(service, folderPath, ct);

            var listReq = service.Files.List();
            listReq.Q = $"'{parentId}' in parents and mimeType != 'application/vnd.google-apps.folder' and trashed = false";
            listReq.Fields = "files(id, name, size, modifiedTime)";

            var response = await listReq.ExecuteAsync(ct);
            foreach (var file in response.Files)
            {
                if (file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    archives.Add(new CloudArchiveItem
                    {
                        Destination = Name,
                        KeyOrId = file.Id,
                        DisplayName = file.Name,
                        CloudFolder = folderPath,
                        SizeBytes = file.Size ?? 0,
                        LastModified = file.ModifiedTimeDateTimeOffset?.UtcDateTime,
                        ServerIpOrTag = config.GDrive.AccountLabel
                    });
                }
            }

            return Result<List<CloudArchiveItem>>.Success(archives.OrderByDescending(a => a.LastModified).ToList());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Google Drive] Failed to list archives: {Error}", ex.Message);
            return Result<List<CloudArchiveItem>>.Failure($"Google Drive list error: {ex.Message}");
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
            using var service = await CreateDriveServiceAsync(ct);
            if (service == null)
                return Result.Failure("Google Drive service unavailable.");

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var getReq = service.Files.Get(keyOrId);
            getReq.Fields = "size";
            var file = await getReq.ExecuteAsync(ct);
            var totalBytes = file.Size ?? 0;

            await using var outputStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write);
            var downloadReq = service.Files.Get(keyOrId);
            downloadReq.MediaDownloader.ProgressChanged += p =>
            {
                progress?.Report((p.BytesDownloaded, totalBytes));
            };

            var downloadStatus = await downloadReq.DownloadAsync(outputStream, ct);
            if (downloadStatus.Status != Google.Apis.Download.DownloadStatus.Completed)
            {
                return Result.Failure(downloadStatus.Exception?.Message ?? "Download failed");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Google Drive] Download failed for Id {Id}: {Error}", keyOrId, ex.Message);
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

        try
        {
            using var service = await CreateDriveServiceAsync(ct);
            if (service == null) return Result<int>.Failure("Google Drive service unavailable.");

            var parentId = await EnsureFolderPathAsync(service, targetFolder, ct);
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            var listReq = service.Files.List();
            listReq.Q = $"'{parentId}' in parents and mimeType != 'application/vnd.google-apps.folder' and trashed = false";
            listReq.Fields = "files(id, name, createdTime)";

            var response = await listReq.ExecuteAsync(ct);
            var deleted = 0;

            foreach (var file in response.Files)
            {
                if (file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    file.CreatedTimeDateTimeOffset?.UtcDateTime < cutoff)
                {
                    await service.Files.Delete(file.Id).ExecuteAsync(ct);
                    deleted++;
                }
            }

            Log.Information("[Google Drive] Pruned {Count} old backups in {Folder}", deleted, targetFolder);
            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Google Drive] Retention cleanup failed: {Error}", ex.Message);
            return Result<int>.Failure($"Drive cleanup error: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        try
        {
            using var service = await CreateDriveServiceAsync(ct);
            if (service == null) return Result.Failure("Missing or invalid Google Drive credentials.");

            await service.Files.Delete(keyOrId).ExecuteAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Google Drive] Delete failed for {Id}: {Error}", keyOrId, ex.Message);
            return Result.Failure($"Drive delete error: {ex.Message}");
        }
    }

    public async Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        try
        {
            using var service = await CreateDriveServiceAsync(ct);
            if (service == null) return Result.Failure("Missing or invalid Google Drive credentials.");

            var listReq = service.Files.List();
            listReq.PageSize = 1;
            await listReq.ExecuteAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Google Drive validation error: {ex.Message}");
        }
    }
}
