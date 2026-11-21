using System.Text.Json.Serialization;
using UniBackup.Core.Common;

namespace UniBackup.Core.Models;

public class FullBackupSchedule
{
    [JsonPropertyName("freq")] public string Freq { get; set; } = "daily"; // daily, weekly, monthly, onetime, manual
    [JsonPropertyName("time")] public string Time { get; set; } = "02:00"; // HH:mm
    [JsonPropertyName("interval")] public int Interval { get; set; } = 1;
    [JsonPropertyName("days")] public List<string> Days { get; set; } = ["MON", "TUE", "WED", "THU", "FRI"];
    [JsonPropertyName("day")] public string Day { get; set; } = "1";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
}

public class IntervalBackupSchedule
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("every_minutes")] public int EveryMinutes { get; set; } = 60;
}

public class ScheduleRunWindow
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("start")] public string Start { get; set; } = "08:00"; // HH:mm
    [JsonPropertyName("end")] public string End { get; set; } = "20:00"; // HH:mm
    [JsonPropertyName("days")] public List<string> Days { get; set; } = ["MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN"];
}

public class ScheduleRetrySettings
{
    [JsonPropertyName("max_attempts")] public int MaxAttempts { get; set; } = 3;
    [JsonPropertyName("wait_minutes")] public int WaitMinutes { get; set; } = 5;
}

public class JobSchedule
{
    [JsonPropertyName("freq")]
    public string Freq { get; set; } = "daily"; // "daily", "weekly", "monthly", "onetime", "logon", "startup", "manual"

    [JsonPropertyName("time")]
    public string Time { get; set; } = "02:00"; // HH:mm (24hr)

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 1;

    [JsonPropertyName("days")]
    public List<string> Days { get; set; } = ["MON", "TUE", "WED", "THU", "FRI"];

    [JsonPropertyName("day")]
    public string Day { get; set; } = "1";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    // Schedule v2 fields
    [JsonPropertyName("full")]
    public FullBackupSchedule? Full { get; set; }

    [JsonPropertyName("diff")]
    public IntervalBackupSchedule? Diff { get; set; }

    [JsonPropertyName("log")]
    public IntervalBackupSchedule? Log { get; set; }

    [JsonPropertyName("window")]
    public ScheduleRunWindow? Window { get; set; }

    [JsonPropertyName("missed_run_lookback_minutes")]
    public int MissedRunLookbackMinutes { get; set; } = 120;

    [JsonPropertyName("retry")]
    public ScheduleRetrySettings Retry { get; set; } = new();

    public FullBackupSchedule GetEffectiveFullSchedule() => Full ?? new FullBackupSchedule
    {
        Freq = Freq,
        Time = Time,
        Interval = Interval,
        Days = Days,
        Day = Day,
        Date = Date
    };

    public string ToDisplayString()
    {
        return Freq.ToLowerInvariant() switch
        {
            "manual" => "Manual only",
            "daily" => Interval > 1 ? $"Every {Interval} days at {Time}" : $"Daily at {Time}",
            "weekly" => $"Weekly on {string.Join(", ", Days)} at {Time}",
            "monthly" => $"Monthly on day {Day} at {Time}",
            "onetime" => $"Once on {Date} at {Time}",
            "logon" => "At user logon",
            "startup" => "At system startup",
            _ => "Not scheduled"
        };
    }
}

public class BackupJob
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source_folder")]
    public string SourceFolder { get; set; } = "";

    [JsonPropertyName("subfolder")]
    public string? Subfolder { get; set; }

    [JsonPropertyName("destinations")]
    public List<string> Destinations { get; set; } = ["s3"]; // "s3", "gdrive", "azure", "local"

    [JsonPropertyName("cloud_folder")]
    public string? CloudFolder { get; set; } // If blank, uses server IP default

    [JsonPropertyName("schedule")]
    public JobSchedule Schedule { get; set; } = new();

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("include_in_run_all")]
    public bool IncludeInRunAll { get; set; } = true;

    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; } = false;

    [JsonPropertyName("use_vss")]
    public bool UseVss { get; set; } = true;

    [JsonPropertyName("retention_days")]
    public int? RetentionDaysOverride { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "folder"; // "folder" | "sqlserver"

    [JsonPropertyName("sql")]
    public SqlServerSource? Sql { get; set; }

    [JsonIgnore]
    public bool IsSqlServer => Kind.Equals("sqlserver", StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("last_run_time")]
    public DateTime? LastRunTime { get; set; }

    [JsonPropertyName("last_run_status")]
    public string? LastRunStatus { get; set; } // "SUCCESS", "PARTIAL", "FAILED"

    [JsonPropertyName("last_run_duration_seconds")]
    public double? LastRunDurationSeconds { get; set; }

    [JsonPropertyName("last_run_size_bytes")]
    public long? LastRunSizeBytes { get; set; }

    [JsonIgnore]
    public string EffectiveSourcePath =>
        string.IsNullOrWhiteSpace(Subfolder) ? SourceFolder : Path.Combine(SourceFolder, Subfolder);

    [JsonIgnore]
    public string TaskName => TaskNameFor(Id);

    public static string TaskNameFor(string jobId) => $"UniBackup_{jobId}";

    public string GenerateArchiveName(DateTime timestamp)
    {
        var cleanName = FormattingUtils.SanitizePathSegment(string.IsNullOrWhiteSpace(Name) ? Path.GetFileName(EffectiveSourcePath) : Name);
        return $"{cleanName}_{timestamp:yyyyMMdd_HHmmss}.zip";
    }
}
