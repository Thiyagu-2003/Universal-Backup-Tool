using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

public class RetentionPolicy
{
    [JsonPropertyName("keep_days")]
    public int KeepDays { get; set; } = 30;

    [JsonPropertyName("gfs_enabled")]
    public bool GfsEnabled { get; set; }

    [JsonPropertyName("gfs_keep_dailies")]
    public int GfsKeepDailies { get; set; } = 7;

    [JsonPropertyName("gfs_keep_weeklies")]
    public int GfsKeepWeeklies { get; set; } = 4;

    [JsonPropertyName("gfs_keep_monthlies")]
    public int GfsKeepMonthlies { get; set; } = 12;

    [JsonPropertyName("object_lock_enabled")]
    public bool ObjectLockEnabled { get; set; }

    [JsonPropertyName("object_lock_mode")]
    public string ObjectLockMode { get; set; } = "governance"; // "governance" or "compliance"

    [JsonPropertyName("object_lock_days")]
    public int ObjectLockDays { get; set; } = 30;
}

public class RetentionAction
{
    public CloudArchiveItem Item { get; set; } = null!;
    public bool Delete { get; set; }
    public string Reason { get; set; } = "";
}

public class SqlRetentionPlan
{
    public List<RetentionAction> Actions { get; set; } = [];
    public List<RetentionAction> Deletions => Actions.Where(a => a.Delete).ToList();
    public List<RetentionAction> Retentions => Actions.Where(a => !a.Delete).ToList();
}
