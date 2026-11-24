using System.Collections.Concurrent;
using System.IO.Compression;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Engine.Services;

public class RecoveryService
{
    private readonly IEnumerable<IStorageProvider> _storageProviders;
    private readonly IArchiveService _archiveService;
    private readonly ConcurrentDictionary<string, string> _cachedArchives = new();

    public RecoveryService(
        IEnumerable<IStorageProvider> storageProviders,
        IArchiveService archiveService)
    {
        _storageProviders = storageProviders;
        _archiveService = archiveService;
    }

    public IEnumerable<IStorageProvider> StorageProviders => _storageProviders;

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

    public async Task<Result<string>> EnsureArchiveDownloadedAsync(
        AppConfig config,
        CloudArchiveItem archiveItem,
        IProgress<(string phase, double percent, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var cacheKey = $"{archiveItem.Destination}_{archiveItem.KeyOrId}";
        if (_cachedArchives.TryGetValue(cacheKey, out var existingPath) && File.Exists(existingPath))
        {
            return Result<string>.Success(existingPath);
        }

        var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(archiveItem.Destination, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
            return Result<string>.Failure($"No storage provider registered for {archiveItem.Destination}");

        var tempDir = config.Backup.TempFolder;
        AppPaths.EnsureWritableDirectory(tempDir, out tempDir);

        var tempZipPath = Path.Combine(tempDir, $"cache_{Guid.NewGuid():N}_{Path.GetFileName(archiveItem.DisplayName)}");

        progress?.Report(("Downloading", 10, $"Downloading {archiveItem.DisplayName} from {provider.Name.ToUpperInvariant()}..."));

        var downloadProgress = new Progress<(long downloaded, long total)>(d =>
        {
            var pct = d.total > 0 ? 10.0 + (d.downloaded / (double)d.total) * 80.0 : 10.0;
            progress?.Report(("Downloading", pct, $"Downloaded: {FormattingUtils.FormatBytes(d.downloaded)} / {FormattingUtils.FormatBytes(d.total)}"));
        });

        var downloadResult = await provider.DownloadAsync(config, archiveItem.KeyOrId, tempZipPath, downloadProgress, ct);
        if (downloadResult.IsFailure)
        {
            return Result<string>.Failure($"Download failed: {downloadResult.Error}");
        }

        _cachedArchives[cacheKey] = tempZipPath;
        return Result<string>.Success(tempZipPath);
    }

    public async Task<Result<ArchiveInspectionResult>> InspectArchiveAsync(
        AppConfig config,
        CloudArchiveItem archiveItem,
        IProgress<(string phase, double percent, string message)>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var dlRes = await EnsureArchiveDownloadedAsync(config, archiveItem, progress, ct);
            if (dlRes.IsFailure) return Result<ArchiveInspectionResult>.Failure(dlRes.Error!);

            var localZipPath = dlRes.Value!;
            progress?.Report(("Inspecting", 92, "Reading archive directory structure..."));

            var entries = new List<ArchiveFileEntry>();
            bool isIis = false;
            string? detectedSite = null;
            long totalUncompressed = 0;

            using (var zip = ZipFile.OpenRead(localZipPath))
            {
                foreach (var e in zip.Entries)
                {
                    bool isDir = string.IsNullOrEmpty(e.Name) || e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                    bool isIisConfig = e.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase);
                    if (isIisConfig) isIis = true;

                    var entry = new ArchiveFileEntry
                    {
                        Name = isDir ? Path.GetFileName(e.FullName.TrimEnd('/', '\\')) : e.Name,
                        RelativePath = e.FullName.Replace('\\', '/'),
                        DirectoryPath = (Path.GetDirectoryName(e.FullName) ?? "").Replace('\\', '/'),
                        SizeBytes = e.Length,
                        LastModified = e.LastWriteTime.DateTime,
                        IsDirectory = isDir,
                        IsIisConfig = isIisConfig
                    };

                    totalUncompressed += e.Length;
                    entries.Add(entry);
                }
            }

            // Detect IIS site name
            var cleanName = Path.GetFileNameWithoutExtension(archiveItem.DisplayName);
            if (cleanName.StartsWith("IIS_", StringComparison.OrdinalIgnoreCase))
            {
                isIis = true;
                detectedSite = cleanName[4..];
            }
            else if (isIis)
            {
                detectedSite = cleanName;
            }

            var result = new ArchiveInspectionResult
            {
                ArchiveDisplayName = archiveItem.DisplayName,
                Provider = archiveItem.Destination,
                LocalZipPath = localZipPath,
                Entries = entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.RelativePath).ToList(),
                IsIisWebsite = isIis,
                DetectedSiteName = detectedSite,
                TotalUncompressedBytes = totalUncompressed
            };

            progress?.Report(("Ready", 100, $"Inspected {entries.Count} entries ({FormattingUtils.FormatBytes(totalUncompressed)})"));
            return Result<ArchiveInspectionResult>.Success(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to inspect archive {Archive}", archiveItem.DisplayName);
            return Result<ArchiveInspectionResult>.Failure($"Archive inspection failed: {ex.Message}");
        }
    }

    public async Task<Result<SelectiveRestoreResult>> RestoreSelectiveAsync(
        AppConfig config,
        CloudArchiveItem archiveItem,
        IEnumerable<string>? selectedRelativePaths,
        string targetRestoreDirectory,
        string writeMode = "safe",
        IProgress<(string phase, double percent, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var provider = _storageProviders.FirstOrDefault(p => p.Name.Equals(archiveItem.Destination, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
            return Result<SelectiveRestoreResult>.Failure($"No storage provider registered for {archiveItem.Destination}");

        try
        {
            var dlRes = await EnsureArchiveDownloadedAsync(config, archiveItem, progress, ct);
            if (dlRes.IsFailure) return Result<SelectiveRestoreResult>.Failure(dlRes.Error!);

            var localZipPath = dlRes.Value!;

            var finalTargetDir = targetRestoreDirectory;
            if (string.Equals(writeMode, "safe", StringComparison.OrdinalIgnoreCase))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                finalTargetDir = Path.Combine(targetRestoreDirectory, $"Restore_{timestamp}");
            }

            Directory.CreateDirectory(finalTargetDir);

            var selectedList = selectedRelativePaths?.ToList() ?? [];
            var selectedSet = new HashSet<string>(selectedList, StringComparer.OrdinalIgnoreCase);
            bool filterActive = selectedSet.Count > 0;

            progress?.Report(("Extracting", 50, $"Extracting to {finalTargetDir}..."));

            int restoredCount = 0;
            long restoredBytes = 0;
            var restoredFiles = new List<string>();

            using (var zip = ZipFile.OpenRead(localZipPath))
            {
                var totalEntries = zip.Entries.Count;
                int currentEntryIdx = 0;

                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    currentEntryIdx++;

                    var normalizedEntry = entry.FullName.Replace('\\', '/');

                    if (filterActive)
                    {
                        bool isMatch = selectedSet.Contains(normalizedEntry) ||
                                       selectedSet.Any(s => normalizedEntry.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

                        if (!isMatch) continue;
                    }

                    var destFile = Path.GetFullPath(Path.Combine(finalTargetDir, entry.FullName));
                    if (!destFile.StartsWith(Path.GetFullPath(finalTargetDir), StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // Zip Slip security check
                    }

                    if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(destFile);
                        continue;
                    }

                    var parentDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    if (File.Exists(destFile))
                    {
                        if (string.Equals(writeMode, "skip", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    entry.ExtractToFile(destFile, overwrite: true);
                    restoredCount++;
                    restoredBytes += entry.Length;
                    restoredFiles.Add(destFile);

                    var pct = 50.0 + ((double)currentEntryIdx / totalEntries) * 50.0;
                    progress?.Report(("Extracting", pct, $"Restored ({restoredCount}): {entry.Name}"));
                }
            }

            progress?.Report(("Complete", 100, $"Successfully restored {restoredCount} files ({FormattingUtils.FormatBytes(restoredBytes)})"));

            return Result<SelectiveRestoreResult>.Success(new SelectiveRestoreResult
            {
                FilesRestored = restoredCount,
                BytesRestored = restoredBytes,
                TargetDirectory = finalTargetDir,
                RestoredFiles = restoredFiles
            });
        }
        catch (OperationCanceledException)
        {
            return Result<SelectiveRestoreResult>.Failure("Restore cancelled by user.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Selective restore error: {Error}", ex.Message);
            return Result<SelectiveRestoreResult>.Failure($"Restore error: {ex.Message}");
        }
    }

    public async Task<Result> RestoreArchiveAsync(
        AppConfig config,
        CloudArchiveItem archiveItem,
        string targetRestoreDirectory,
        string writeMode = "safe",
        IProgress<(string phase, double percent, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var res = await RestoreSelectiveAsync(config, archiveItem, null, targetRestoreDirectory, writeMode, progress, ct);
        return res.IsSuccess ? Result.Success() : Result.Failure(res.Error!);
    }

    public async Task<Result<string>> ReadEntryPreviewAsync(string localZipPath, string relativePath, int maxBytes = 64 * 1024, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(localZipPath)) return Result<string>.Failure("Archive cache file not found.");

            using var zip = ZipFile.OpenRead(localZipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Result<string>.Failure($"Entry '{relativePath}' not found in archive.");

            await using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var buffer = new char[maxBytes / 2];
            int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, read);
            return Result<string>.Success(content);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure($"Failed to read entry preview: {ex.Message}");
        }
    }

    public void ClearArchiveCache()
    {
        foreach (var file in _cachedArchives.Values)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }
        _cachedArchives.Clear();
    }
}
