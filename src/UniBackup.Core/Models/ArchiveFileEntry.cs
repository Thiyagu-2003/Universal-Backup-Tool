namespace UniBackup.Core.Models;

public class ArchiveFileEntry
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? LastModified { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsSelected { get; set; }
    public bool IsIisConfig { get; set; }

    public string SizeDisplay => FormatBytes(SizeBytes);
    public string DateDisplay => LastModified.HasValue ? LastModified.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
