using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Engine.Services;

public class RecoveryService
{
    private readonly IEnumerable<IStorageProvider> _storageProviders;
    private readonly IArchiveService _archiveService;

    public RecoveryService(
        IEnumerable<IStorageProvider> storageProviders,
        IArchiveService archiveService)
    {
        _storageProviders = storageProviders;
        _archiveService = archiveService;
    }

    public async Task<Result<List<CloudArchiveItem>>> ListAllArchivesAsync(
        AppConfig config,
        string? filterDestination = null,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var all = new List<CloudArchiveItem>();

        foreach (var provider in _storageProviders)
        {
            if (!string.IsNullOrEmpty(filterDestination) &&
                !provider.Name.Equals(filterDestination, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!provider.IsEnabled(config)) continue;

            var listResult = await provider.ListArchivesAsync(config, folderPrefix, ct);
            if (listResult.IsSuccess)
            {
                all.AddRange(listResult.Value);
            }
            else
            {
                Log.Warning("Could not list archives from {Provider}: {Error}", provider.Name, listResult.Error);
            }
        }

        return Result<List<CloudArchiveItem>>.Success(all.OrderByDescending(a => a.LastModified).ToList());
    }

    public async Task<Result> RestoreArchiveAsync(
        AppConfig config,
        CloudArchiveItem archiveItem,
        string targetRestoreDirectory,
        string writeMode = "safe",
        IProgress<(string phase, double percent, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(archiveItem.Destination, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
            return Result.Failure($"No storage provider registered for {archiveItem.Destination}");

        var tempDir = config.Backup.TempFolder;
        AppPaths.EnsureWritableDirectory(tempDir, out tempDir);

        var tempZipPath = Path.Combine(tempDir, $"restore_{Guid.NewGuid():N}.zip");

        try
        {
            progress?.Report(("Downloading", 10, $"Downloading {archiveItem.DisplayName} from {provider.Name.ToUpperInvariant()}..."));

            var downloadProgress = new Progress<(long downloaded, long total)>(d =>
            {
                var pct = d.total > 0 ? 10.0 + (d.downloaded / (double)d.total) * 40.0 : 10.0;
                progress?.Report(("Downloading", pct, $"Downloaded: {FormattingUtils.FormatBytes(d.downloaded)} / {FormattingUtils.FormatBytes(d.total)}"));
            });

            var downloadResult = await provider.DownloadAsync(config, archiveItem.KeyOrId, tempZipPath, downloadProgress, ct);
            if (downloadResult.IsFailure)
            {
                return Result.Failure($"Download failed: {downloadResult.Error}");
            }

            progress?.Report(("Extracting", 50, $"Extracting files to {targetRestoreDirectory} ({writeMode} mode)..."));

            var extractProgress = new Progress<(int count, int total, string file)>(e =>
            {
                var pct = e.total > 0 ? 50.0 + (e.count / (double)e.total) * 50.0 : 50.0;
                progress?.Report(("Extracting", pct, $"Extracted: {e.count}/{e.total} - {e.file}"));
            });

            var extractResult = await _archiveService.ExtractZipArchiveAsync(tempZipPath, targetRestoreDirectory, writeMode, extractProgress, ct);
            if (extractResult.IsFailure)
            {
                return Result.Failure($"Restore extraction failed: {extractResult.Error}");
            }

            progress?.Report(("Complete", 100, "Restore completed successfully!"));
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Restore cancelled by user.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore error: {Error}", ex.Message);
            return Result.Failure($"Restore error: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { /* Ignore */ }
            }
        }
    }
}
