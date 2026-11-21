using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class SftpStorageProvider : IStorageProvider
{
    public string Name => "sftp";

    public bool IsEnabled(AppConfig config) => config.Sftp.Enabled;

    private SftpClient CreateClient(AppConfig config)
    {
        var port = config.Sftp.Port > 0 ? config.Sftp.Port : 22;
        AuthenticationMethod authMethod;

        if (!string.IsNullOrWhiteSpace(config.Sftp.PrivateKeyPath) && File.Exists(config.Sftp.PrivateKeyPath))
        {
            var keyFile = new PrivateKeyFile(config.Sftp.PrivateKeyPath, string.IsNullOrEmpty(config.Sftp.Password) ? null : config.Sftp.Password);
            authMethod = new PrivateKeyAuthenticationMethod(config.Sftp.Username, keyFile);
        }
        else
        {
            authMethod = new PasswordAuthenticationMethod(config.Sftp.Username, config.Sftp.Password);
        }

        var connInfo = new ConnectionInfo(config.Sftp.Host, port, config.Sftp.Username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new SftpClient(connInfo);
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remoteDirectory)
    {
        var parts = remoteDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
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
            await Task.Run(() => client.Connect(), ct);
            EnsureRemoteDirectory(client, folder);

            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            await using var fs = File.OpenRead(localFilePath);
            await Task.Run(() =>
            {
                client.UploadFile(fs, remotePath, canOverride: true, uploaded =>
                {
                    progress?.Report(((long)uploaded, totalBytes));
                });
            }, ct);

            sw.Stop();
            result.Status = "SUCCESS";
            result.BytesUploaded = totalBytes;
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.TargetLocation = $"sftp://{config.Sftp.Host}{remotePath}";

            Log.Information("[SFTP] Uploaded {File} ({Bytes:N0} bytes) to {Path} in {Duration:F1}s",
                fileName, totalBytes, remotePath, sw.Elapsed.TotalSeconds);

            return Result<DestinationRunResult>.Success(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[SFTP] Failed uploading {File} to {Path}", fileName, remotePath);
            return Result<DestinationRunResult>.Failure($"SFTP upload failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    public async Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var items = new List<CloudArchiveItem>();
        var folder = folderPrefix ?? config.Sftp.RemoteFolder;
        using var client = CreateClient(config);

        try
        {
            await Task.Run(() => client.Connect(), ct);
            if (!client.Exists(folder))
            {
                return Result<List<CloudArchiveItem>>.Success(items);
            }

            var files = client.ListDirectory(folder);
            foreach (var file in files)
            {
                if (!file.IsDirectory && !file.IsSymbolicLink)
                {
                    items.Add(new CloudArchiveItem
                    {
                        KeyOrId = file.FullName,
                        DisplayName = file.Name,
                        CloudFolder = folder,
                        LastModified = file.LastWriteTimeUtc,
                        SizeBytes = file.Length,
                        Destination = Name
                    });
                }
            }

            return Result<List<CloudArchiveItem>>.Success(items.OrderByDescending(i => i.LastModified).ToList());
        }
        catch (Exception ex)
        {
            return Result<List<CloudArchiveItem>>.Failure($"SFTP list archives failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
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
            await Task.Run(() => client.Connect(), ct);
            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var fs = File.Create(destinationFilePath);
            await Task.Run(() => client.DownloadFile(keyOrId, fs), ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"SFTP download failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    public async Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            await Task.Run(() => client.Connect(), ct);
            if (client.Exists(keyOrId))
            {
                client.DeleteFile(keyOrId);
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"SFTP delete failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
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
            await Task.Run(() => client.Connect(), ct);
            if (!client.Exists(targetFolder)) return Result<int>.Success(0);

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            var files = client.ListDirectory(targetFolder);

            foreach (var f in files)
            {
                if (!f.IsDirectory && f.LastWriteTimeUtc < cutoff)
                {
                    client.DeleteFile(f.FullName);
                    deleted++;
                }
            }

            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            return Result<int>.Failure($"SFTP retention cleanup failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    public async Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            await Task.Run(() => client.Connect(), ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"SFTP connection failed: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }
}
