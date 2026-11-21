using UniBackup.Core.Common;

namespace UniBackup.Core.Interfaces;

public interface IArchiveService
{
    Task<Result<long>> CreateZipArchiveAsync(
        string sourcePath,
        string outputZipPath,
        bool useVss = false,
        IProgress<(long processedBytes, long totalBytes, string currentFile)>? progress = null,
        CancellationToken ct = default);

    Task<Result> ExtractZipArchiveAsync(
        string zipFilePath,
        string destinationPath,
        string writeMode = "safe", // "overwrite", "missing", "safe"
        IProgress<(int extractedCount, int totalCount, string currentFile)>? progress = null,
        CancellationToken ct = default);
}
