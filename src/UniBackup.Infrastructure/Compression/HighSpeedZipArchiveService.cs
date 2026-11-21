using System.Buffers;
using System.IO.Compression;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using Serilog;

namespace UniBackup.Infrastructure.Compression;

public class HighSpeedZipArchiveService : IArchiveService
{
    private const int BufferSize = 128 * 1024; // 128 KB buffer

    public async Task<Result<long>> CreateZipArchiveAsync(
        string sourcePath,
        string outputZipPath,
        bool useVss = false,
        IProgress<(long processedBytes, long totalBytes, string currentFile)>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
            return Result<long>.Failure($"Source folder does not exist: {sourcePath}");

        var outputDir = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        try
        {
            // 1. Enumerate files and calculate total size
            var dirInfo = new DirectoryInfo(sourcePath);
            var allFiles = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            var totalBytes = allFiles.Sum(f => f.Length);
            long processedBytes = 0;

            Log.Information("Archiving {FileCount} files ({TotalBytes}) from {Source} to {Destination}",
                allFiles.Count, FormattingUtils.FormatBytes(totalBytes), sourcePath, outputZipPath);

            // 2. Open zip archive with optimal compression
            await using var zipFileStream = new FileStream(
                outputZipPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: BufferSize,
                useAsync: true);

            using var zipArchive = new ZipArchive(zipFileStream, ZipArchiveMode.Create, leaveOpen: false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            try
            {
                foreach (var file in allFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(sourcePath, file.FullName).Replace('\\', '/');
                    progress?.Report((processedBytes, totalBytes, relativePath));

                    var entry = zipArchive.CreateEntry(relativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = file.LastWriteTimeUtc;

                    await using var entryStream = entry.Open();
                    
                    // Open with FileShare.ReadWrite to allow reading files currently in use by IIS
                    FileStream? inputStream = null;
                    try
                    {
                        inputStream = new FileStream(
                            file.FullName,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            bufferSize: BufferSize,
                            useAsync: true);
                    }
                    catch (IOException ex)
                    {
                        Log.Warning(ex, "Could not open locked file {FilePath}. Attempting secondary retry with permissive sharing.", file.FullName);
                        try
                        {
                            await Task.Delay(100, ct);
                            inputStream = new FileStream(
                                file.FullName,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite,
                                bufferSize: BufferSize,
                                useAsync: true);
                        }
                        catch (Exception innerEx)
                        {
                            Log.Error(innerEx, "Failed to include locked file: {FilePath}", file.FullName);
                            continue; // Skip file if permanently unreadable
                        }
                    }

                    if (inputStream != null)
                    {
                        await using (inputStream)
                        {
                            int bytesRead;
                            while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                            {
                                await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                                processedBytes += bytesRead;
                                progress?.Report((processedBytes, totalBytes, relativePath));
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var finalZipInfo = new FileInfo(outputZipPath);
            Log.Information("Compression completed: {OutputZipPath} ({Size})",
                outputZipPath, FormattingUtils.FormatBytes(finalZipInfo.Length));

            return Result<long>.Success(finalZipInfo.Length);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);
            return Result<long>.Failure("Compression cancelled by user.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create zip archive: {Path}", outputZipPath);
            if (File.Exists(outputZipPath))
                File.Delete(outputZipPath);
            return Result<long>.Failure($"Zip creation error: {ex.Message}");
        }
    }

    public async Task<Result> ExtractZipArchiveAsync(
        string zipFilePath,
        string destinationPath,
        string writeMode = "safe",
        IProgress<(int extractedCount, int totalCount, string currentFile)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(zipFilePath))
            return Result.Failure($"Archive file not found: {zipFilePath}");

        try
        {
            var targetDir = destinationPath;
            if (writeMode.Equals("safe", StringComparison.OrdinalIgnoreCase))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                targetDir = Path.Combine(destinationPath, $"Restore_{timestamp}");
            }

            Directory.CreateDirectory(targetDir);

            using var zip = ZipFile.OpenRead(zipFilePath);
            var totalCount = zip.Entries.Count;
            var extractedCount = 0;

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var destFileName = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                // Directory traversal vulnerability defense
                if (!destFileName.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                    continue;

                progress?.Report((extractedCount, totalCount, entry.FullName));

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destFileName);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(destFileName);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                if (File.Exists(destFileName))
                {
                    if (writeMode.Equals("missing", StringComparison.OrdinalIgnoreCase))
                    {
                        extractedCount++;
                        continue; // Skip existing file
                    }
                }

                entry.ExtractToFile(destFileName, overwrite: true);
                extractedCount++;
                progress?.Report((extractedCount, totalCount, entry.FullName));
            }

            Log.Information("Extracted {Count} entries to {TargetDir}", extractedCount, targetDir);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract zip archive: {Path}", zipFilePath);
            return Result.Failure($"Extraction error: {ex.Message}");
        }
    }
}
