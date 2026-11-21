using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

public class JobRunState
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = "";

    [JsonPropertyName("last_full_time")]
    public DateTimeOffset? LastFullTime { get; set; }

    [JsonPropertyName("last_full_file")]
    public string? LastFullFile { get; set; }

    [JsonPropertyName("last_diff_time")]
    public DateTimeOffset? LastDiffTime { get; set; }

    [JsonPropertyName("last_diff_file")]
    public string? LastDiffFile { get; set; }

    [JsonPropertyName("last_log_time")]
    public DateTimeOffset? LastLogTime { get; set; }

    [JsonPropertyName("last_log_file")]
    public string? LastLogFile { get; set; }

    [JsonPropertyName("last_run_time")]
    public DateTimeOffset? LastRunTime { get; set; }

    [JsonPropertyName("last_run_status")]
    public string? LastRunStatus { get; set; } // SUCCESS, PARTIAL, FAILED

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; }

    [JsonPropertyName("next_retry_time")]
    public DateTimeOffset? NextRetryTime { get; set; }

    /// <summary>
    /// Tracks per-database the last full backup file that succeeded upload to ALL destinations.
    /// Key: Database name, Value: file name (e.g. Shop_FULL_20261121_020000.bak)
    /// </summary>
    [JsonPropertyName("uploaded_bases")]
    public Dictionary<string, string> UploadedBases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
