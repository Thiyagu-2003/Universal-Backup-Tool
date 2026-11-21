namespace UniBackup.Core.Models;

public class BackupProgressInfo
{
    public string Phase { get; set; } = "Starting"; // "Preflight", "Compressing", "Uploading", "Retention", "Finalizing"
    public double Percent { get; set; }
    public string StatusMessage { get; set; } = "";
    public string? CurrentFile { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
}

public record ActiveRunInfo(
    string RunId,
    string JobId,
    string JobName,
    DateTime StartTime,
    string Status,
    BackupProgressInfo? Progress);

public record ProgressEvent(
    string RunId,
    string JobId,
    DateTime Timestamp,
    BackupProgressInfo Progress);
