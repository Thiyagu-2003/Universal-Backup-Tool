using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

public class RecoveryOperationRecord
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = $"REC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("source_backup")]
    public string SourceBackup { get; set; } = "";

    [JsonPropertyName("target_name")]
    public string TargetName { get; set; } = "";

    [JsonPropertyName("recovery_type")]
    public string RecoveryType { get; set; } = "Files"; // "SqlPointInTime", "Files", "IisWebsite", "RestoreDrill"

    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("end_time")]
    public DateTime? EndTime { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "RUNNING"; // "SUCCESS", "FAILED", "RUNNING", "CANCELLED"

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("log_details")]
    public string? LogDetails { get; set; }

    [JsonPropertyName("items_restored")]
    public int ItemsRestored { get; set; }

    [JsonPropertyName("bytes_restored")]
    public long BytesRestored { get; set; }

    // UI Formatting Helpers
    public string StartTimeDisplay => StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string EndTimeDisplay => EndTime.HasValue ? EndTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-";
    public string DurationDisplay => DurationSeconds > 0 ? $"{DurationSeconds:F1}s" : "-";
    public string BytesRestoredDisplay => FormatBytes(BytesRestored);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
