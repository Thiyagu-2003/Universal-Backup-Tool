using System.Text.RegularExpressions;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Sql;

/// Every statement takes its values as @db / @path parameters; no identifier is ever concatenated into SQL.
public static partial class SqlBackupText
{
    public static string Backup(SqlBackupType type, SqlServerSource options, bool compression)
    {
        var with = new List<string>();
        if (type == SqlBackupType.Diff) with.Add("DIFFERENTIAL");
        with.Add("INIT");
        if (options.CopyOnly && type != SqlBackupType.Diff) with.Add("COPY_ONLY"); // no effect on DIFFERENTIAL
        if (options.Checksum) with.Add("CHECKSUM");
        if (compression) with.Add("COMPRESSION");
        with.Add("STATS = 5");

        var verb = type == SqlBackupType.Log ? "BACKUP LOG" : "BACKUP DATABASE";
        return $"{verb} @db TO DISK = @path WITH {string.Join(", ", with)};";
    }

    public static string Verify(bool checksum) =>
        checksum ? "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;" : "RESTORE VERIFYONLY FROM DISK = @path;";

    public static string CheckDb(bool physicalOnly) =>
        physicalOnly ? "DBCC CHECKDB (@db) WITH NO_INFOMSGS, PHYSICAL_ONLY;" : "DBCC CHECKDB (@db) WITH NO_INFOMSGS;";

    public const string ListDatabases =
        "SELECT name, state_desc, recovery_model_desc, is_read_only, database_id FROM sys.databases WHERE name <> 'tempdb' ORDER BY name;";

    // NULL differential_base_lsn = no full to diff against; NULL last_log_backup_lsn = log chain not started.
    // Columns 3-4: device of the newest non-copy-only FULL, and of the newest non-copy-only FULL or LOG, taken by any tool.
    public const string ChainState =
        "SELECT (SELECT TOP (1) differential_base_lsn FROM sys.master_files WHERE database_id = DB_ID(@db) AND type = 0), " +
        "(SELECT last_log_backup_lsn FROM sys.database_recovery_status WHERE database_id = DB_ID(@db)), " +
        "(SELECT TOP (1) mf.physical_device_name FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id " +
        "WHERE bs.database_name = @db AND bs.type = 'D' AND bs.is_copy_only = 0 ORDER BY bs.backup_set_id DESC), " +
        "(SELECT TOP (1) mf.physical_device_name FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id " +
        "WHERE bs.database_name = @db AND bs.type IN ('D', 'L') AND bs.is_copy_only = 0 ORDER BY bs.backup_set_id DESC);";

    public const string ServerFacts =
        "SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(256)), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));";

    public static bool TryParsePercent(string message, out int percent)
    {
        var m = PercentPattern().Match(message);
        percent = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        return m.Success;
    }

    [GeneratedRegex(@"^(\d{1,3}) percent processed", RegexOptions.IgnoreCase)]
    private static partial Regex PercentPattern();
}
