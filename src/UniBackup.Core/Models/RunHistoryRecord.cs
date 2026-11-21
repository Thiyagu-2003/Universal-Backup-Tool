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
}
