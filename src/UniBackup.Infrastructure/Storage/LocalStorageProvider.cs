using System.Diagnostics;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class LocalStorageProvider : IStorageProvider
{
    public string Name => "local";

    public bool IsEnabled(AppConfig config) => config.Local.Enabled;

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

        var targetBase = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
        var fileName = Path.GetFileName(localFilePath);
        var targetFilePath = Path.Combine(targetBase, fileName);

        var sw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(targetBase);

            var fileInfo = new FileInfo(localFilePath);
            var totalBytes = fileInfo.Length;

            await using var sourceStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var targetStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
            {
                await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                progress?.Report((totalRead, totalBytes));
            }

            sw.Stop();
            result.Status = "SUCCESS";
            result.BytesUploaded = totalBytes;
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.TargetLocation = targetFilePath;

            Log.Information("[Local Storage] Copied {File} to {Target} in {Duration:F1}s", fileName, targetFilePath, sw.Elapsed.TotalSeconds);
            return Result<DestinationRunResult>.Success(result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[Local Storage] Failed copying {File} to {Target}", fileName, targetFilePath);
            return Result<DestinationRunResult>.Failure($"Local storage upload failed: {ex.Message}");
        }
    }

    public Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var items = new List<CloudArchiveItem>();
        var targetDir = folderPrefix ?? config.Local.TargetPath;

        if (!Directory.Exists(targetDir))
        {
            return Task.FromResult(Result<List<CloudArchiveItem>>.Success(items));
        }

        try
        {
            var files = Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".trn", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ubenc", StringComparison.OrdinalIgnoreCase));

            foreach (var filePath in files)
            {
                var fi = new FileInfo(filePath);
                items.Add(new CloudArchiveItem
                {
                    KeyOrId = filePath,
                    DisplayName = fi.Name,
                    CloudFolder = Path.GetDirectoryName(filePath) ?? targetDir,
                    LastModified = fi.LastWriteTimeUtc,
                    SizeBytes = fi.Length,
                    Destination = Name
                });
            }

            return Task.FromResult(Result<List<CloudArchiveItem>>.Success(items.OrderByDescending(i => i.LastModified).ToList()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<List<CloudArchiveItem>>.Failure($"Failed listing local archives: {ex.Message}"));
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
            if (!File.Exists(keyOrId)) return Result.Failure($"Source file not found: {keyOrId}");

            var fi = new FileInfo(keyOrId);
            var total = fi.Length;

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var src = new FileStream(keyOrId, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var dst = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buf = new byte[81920];
            long totalRead = 0;
            int r;
            while ((r = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, r), ct);
                totalRead += r;
                progress?.Report((totalRead, total));
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Local download failed: {ex.Message}");
        }
    }

    public Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(keyOrId))
            {
                File.Delete(keyOrId);
            }
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Local delete failed: {ex.Message}"));
        }
    }

    public Task<Result<int>> CleanupRetentionAsync(
        AppConfig config,
        string targetFolder,
        int retentionDays,
        CancellationToken ct = default)
    {
        var deleted = 0;
        if (!Directory.Exists(targetFolder) || retentionDays <= 0)
            return Task.FromResult(Result<int>.Success(0));

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        try
        {
            var files = Directory.GetFiles(targetFolder, "*.*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var fi = new FileInfo(f);
                if (fi.LastWriteTimeUtc < cutoff)
                {
                    try
                    {
                        File.Delete(f);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Local Storage] Could not delete expired backup {File}", f);
                    }
                }
            }
            return Task.FromResult(Result<int>.Success(deleted));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<int>.Failure($"Local retention cleanup failed: {ex.Message}"));
        }
    }

    public Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        try
        {
            var dir = config.Local.TargetPath;
            if (string.IsNullOrWhiteSpace(dir)) return Task.FromResult(Result.Failure("Target path is not set."));

            Directory.CreateDirectory(dir);
            var testFile = Path.Combine(dir, $".ub_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Local target path inaccessible: {ex.Message}"));
        }
    }
}
