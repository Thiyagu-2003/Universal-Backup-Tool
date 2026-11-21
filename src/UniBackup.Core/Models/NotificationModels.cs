using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

public class NotificationTriggerMatrix
{
    [JsonPropertyName("full_success")] public bool FullSuccess { get; set; } = false;
    [JsonPropertyName("full_warning")] public bool FullWarning { get; set; } = true;
    [JsonPropertyName("full_failure")] public bool FullFailure { get; set; } = true;

    [JsonPropertyName("diff_success")] public bool DiffSuccess { get; set; } = false;
    [JsonPropertyName("diff_warning")] public bool DiffWarning { get; set; } = true;
    [JsonPropertyName("diff_failure")] public bool DiffFailure { get; set; } = true;

    [JsonPropertyName("log_success")] public bool LogSuccess { get; set; } = false;
    [JsonPropertyName("log_warning")] public bool LogWarning { get; set; } = true;
    [JsonPropertyName("log_failure")] public bool LogFailure { get; set; } = true;

    public bool ShouldNotify(SqlBackupType? type, string status)
    {
        var isSuccess = status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
        var isWarning = status.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("WARNING", StringComparison.OrdinalIgnoreCase);
        var isFailure = status.Equals("FAILED", StringComparison.OrdinalIgnoreCase);

        var effectiveType = type ?? SqlBackupType.Full;
        return effectiveType switch
        {
            SqlBackupType.Full => (isSuccess && FullSuccess) || (isWarning && FullWarning) || (isFailure && FullFailure),
            SqlBackupType.Diff => (isSuccess && DiffSuccess) || (isWarning && DiffWarning) || (isFailure && DiffFailure),
            SqlBackupType.Log => (isSuccess && LogSuccess) || (isWarning && LogWarning) || (isFailure && LogFailure),
            _ => (isWarning || isFailure)
        };
    }
}

public class HeartbeatSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("fail_url")] public string? FailUrl { get; set; }
    [JsonPropertyName("append_fail_path")] public bool AppendFailPath { get; set; } = true; // https://hc-ping.com/UUID/fail
}

public class RpoSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("default_rpo_minutes")] public int DefaultRpoMinutes { get; set; } = 60;
    [JsonPropertyName("database_rpo_minutes")] public Dictionary<string, int> DatabaseRpoMinutes { get; set; } = [];
}

public class PrometheusSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
    [JsonPropertyName("port")] public int Port { get; set; } = 9114;
    [JsonPropertyName("path")] public string Path { get; set; } = "/metrics";
}
