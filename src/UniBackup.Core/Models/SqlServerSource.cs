using System.Text.Json.Serialization;

namespace UniBackup.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<SqlBackupType>))]
public enum SqlBackupType { Full, Diff, Log }

public class SqlServerSource
{
    [JsonPropertyName("server")] public string Server { get; set; } = "localhost";
    [JsonPropertyName("use_windows_auth")] public bool UseWindowsAuth { get; set; } = true;
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password_sealed")] public string? PasswordSealed { get; set; } // ICredentialProtector.Seal
    [JsonPropertyName("encrypt")] public bool Encrypt { get; set; } = true;
    [JsonPropertyName("trust_server_certificate")] public bool TrustServerCertificate { get; set; }
    [JsonPropertyName("all_user_databases")] public bool AllUserDatabases { get; set; } = true;
    [JsonPropertyName("databases")] public List<string> Databases { get; set; } = [];
    [JsonPropertyName("exclude_patterns")] public List<string> ExcludePatterns { get; set; } = []; // wildcards * ?
    [JsonPropertyName("copy_only")] public bool CopyOnly { get; set; }
    [JsonPropertyName("checksum")] public bool Checksum { get; set; } = true;
    [JsonPropertyName("native_compression")] public bool NativeCompression { get; set; } = true;
    [JsonPropertyName("verify")] public bool Verify { get; set; } = true;
    [JsonPropertyName("run_checkdb")] public bool RunCheckDb { get; set; }
    [JsonPropertyName("checkdb_physical_only")] public bool CheckDbPhysicalOnly { get; set; }
    [JsonPropertyName("staging_folder")] public string? StagingFolder { get; set; } // blank = instance default backup path
    [JsonPropertyName("command_timeout_seconds")] public int CommandTimeoutSeconds { get; set; } // 0 = no timeout
}

public class SqlDatabaseRunResult
{
    [JsonPropertyName("database")] public string Database { get; set; } = "";
    [JsonPropertyName("backup_type")] public SqlBackupType BackupType { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("uploaded")] public bool Uploaded { get; set; } // true only when every destination received the file
    [JsonPropertyName("status")] public string Status { get; set; } = "SUCCESS"; // SUCCESS, FAILED, SKIPPED
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public record SqlDatabaseInfo(string Name, string State, string RecoveryModel, bool IsReadOnly, bool IsSystem);

/// LastFullFile / LastFullOrLogFile: device path of the newest non-copy-only FULL (and FULL-or-LOG) backup in msdb,
/// whichever tool took it. Used to check that a DIFF/LOG builds on a file that is actually in the cloud.
public record SqlChainState(bool HasDifferentialBase, bool HasLogChain, string? LastFullFile = null, string? LastFullOrLogFile = null);
