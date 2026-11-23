using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

public class DestinationRunResult
{
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = ""; // "s3", "gdrive", etc.

    [JsonPropertyName("status")]
    public string Status { get; set; } = "SUCCESS"; // "SUCCESS", "FAILED"

    [JsonPropertyName("target_location")]
    public string TargetLocation { get; set; } = "";

    [JsonPropertyName("bytes_uploaded")]
    public long BytesUploaded { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}

public class RunHistoryRecord
{
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = "";

    [JsonPropertyName("job_name")]
    public string JobName { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("source_folder")]
    public string SourceFolder { get; set; } = "";

    [JsonPropertyName("archive_name")]
    public string ArchiveName { get; set; } = "";

    [JsonPropertyName("archive_size_bytes")]
    public long ArchiveSizeBytes { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "SUCCESS"; // "SUCCESS", "PARTIAL", "FAILED"

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "folder"; // "folder", "sqlserver"

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = "Schedule"; // "Schedule", "Manual", "NightlyBatch", "Cli"

    [JsonPropertyName("destinations")]
    public List<DestinationRunResult> Destinations { get; set; } = [];

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("log_file")]
    public string? LogFile { get; set; }

    [JsonPropertyName("sql_databases")]
    public List<SqlDatabaseRunResult> SqlDatabases { get; set; } = [];
}

public class CloudArchiveItem
{
    public string Destination { get; set; } = "s3";
    public string KeyOrId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CloudFolder { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? LastModified { get; set; }
    public string ServerIpOrTag { get; set; } = "";

    // Convenience formatting properties for UI bindings
    public string FileName => string.IsNullOrEmpty(DisplayName) ? KeyOrId : DisplayName;
    public string SizeDisplay => FormatBytes(SizeBytes);
    public string UploadedTimeDisplay => LastModified.HasValue ? LastModified.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
