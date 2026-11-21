using System.Diagnostics;
using FluentFTP;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class FtpStorageProvider : IStorageProvider
{
    public string Name => "ftp";

    public bool IsEnabled(AppConfig config) => config.Ftp.Enabled;

    private AsyncFtpClient CreateClient(AppConfig config)
    {
        var port = config.Ftp.Port > 0 ? config.Ftp.Port : 21;
        var client = new AsyncFtpClient(config.Ftp.Host, config.Ftp.Username, config.Ftp.Password, port);

        if (config.Ftp.UseFtps)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.Config.ValidateAnyCertificate = true; // enterprise self-signed FTPS support
        }
        else
        {
            client.Config.EncryptionMode = FtpEncryptionMode.None;
        }

        return client;
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
        var remotePath = $"{folder.TrimEnd('/')}/{fileName}";

        var sw = Stopwatch.StartNew();
        using var client = CreateClient(config);

        try
        {
            await client.AutoConnect(ct);
            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            var ftpProgress = new Progress<FtpProgress>(p =>
            {
                progress?.Report(((long)(totalBytes * (p.Progress / 100.0)), totalBytes));
            });

            var status = await client.UploadFile(
                localFilePath,
                remotePath,
                FtpRemoteExists.Overwrite,
                createRemoteDir: true,
                token: ct,
                progress: ftpProgress);

            sw.Stop();
            if (status == FtpStatus.Success)
            {
                result.Status = "SUCCESS";
                result.BytesUploaded = totalBytes;
                result.DurationSeconds = sw.Elapsed.TotalSeconds;
                result.TargetLocation = $"ftp://{config.Ftp.Host}{remotePath}";

                Log.Information("[FTP] Uploaded {File} ({Bytes:N0} bytes) to {Path} in {Duration:F1}s",
                    fileName, totalBytes, remotePath, sw.Elapsed.TotalSeconds);

                return Result<DestinationRunResult>.Success(result);
            }

            return Result<DestinationRunResult>.Failure($"FTP upload returned status: {status}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[FTP] Failed uploading {File} to {Path}", fileName, remotePath);
            return Result<DestinationRunResult>.Failure($"FTP upload failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(CancellationToken.None);
        }
    }

    public async Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var items = new List<CloudArchiveItem>();
        var folder = folderPrefix ?? config.Ftp.RemoteFolder;
        using var client = CreateClient(config);

        try
        {
            await client.AutoConnect(ct);
            if (!await client.DirectoryExists(folder, ct))
            {
                return Result<List<CloudArchiveItem>>.Success(items);
            }

            var list = await client.GetListing(folder, FtpListOption.AllFiles, ct);
            foreach (var item in list)
            {
                if (item.Type == FtpObjectType.File)
                {
                    items.Add(new CloudArchiveItem
                    {
                        KeyOrId = item.FullName,
                        DisplayName = item.Name,
                        CloudFolder = Path.GetDirectoryName(item.FullName)?.Replace('\\', '/') ?? folder,
                        LastModified = item.Modified.ToUniversalTime(),
                        SizeBytes = item.Size,
                        Destination = Name
                    });
                }
            }

            return Result<List<CloudArchiveItem>>.Success(items.OrderByDescending(i => i.LastModified).ToList());
        }
        catch (Exception ex)
        {
            return Result<List<CloudArchiveItem>>.Failure($"FTP list archives failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(CancellationToken.None);
        }
    }

    public async Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            await client.AutoConnect(ct);
            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var status = await client.DownloadFile(destinationFilePath, keyOrId, FtpLocalExists.Overwrite, token: ct);
            return status == FtpStatus.Success ? Result.Success() : Result.Failure($"FTP download returned status: {status}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"FTP download failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(CancellationToken.None);
        }
    }

    public async Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            await client.AutoConnect(ct);
            await client.DeleteFile(keyOrId, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"FTP delete failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(CancellationToken.None);
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

        using var client = CreateClient(config);
        try
        {
            await client.AutoConnect(ct);
            if (!await client.DirectoryExists(targetFolder, ct)) return Result<int>.Success(0);

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var list = await client.GetListing(targetFolder, FtpListOption.AllFiles, ct);

            foreach (var item in list)
            {
                if (item.Type == FtpObjectType.File && item.Modified.ToUniversalTime() < cutoff)
                {
                    await client.DeleteFile(item.FullName, ct);
                    deleted++;
                }
            }

            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            return Result<int>.Failure($"FTP retention cleanup failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(CancellationToken.None);
        }
    }

    public async Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            await client.AutoConnect(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"FTP connection failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) await client.Disconnect(CancellationToken.None);
        }
    }
}
