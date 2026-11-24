namespace UniBackup.Core.Models;

public class ArchiveInspectionResult
{
    public string ArchiveDisplayName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string LocalZipPath { get; set; } = "";
    public List<ArchiveFileEntry> Entries { get; set; } = [];
    public bool IsIisWebsite { get; set; }
    public string? DetectedSiteName { get; set; }
    public string? DetectedOriginalPath { get; set; }
    public long TotalUncompressedBytes { get; set; }
    public int TotalFiles => Entries.Count(e => !e.IsDirectory);
    public int TotalDirectories => Entries.Count(e => e.IsDirectory);

    public string TotalUncompressedSizeDisplay => FormatBytes(TotalUncompressedBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public class SelectiveRestoreResult
{
    public int FilesRestored { get; set; }
    public long BytesRestored { get; set; }
    public string TargetDirectory { get; set; } = "";
    public List<string> RestoredFiles { get; set; } = [];
}
