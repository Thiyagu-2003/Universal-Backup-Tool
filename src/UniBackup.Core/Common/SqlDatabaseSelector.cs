using System.Text.RegularExpressions;
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class SqlDatabaseSelector
{
    public static (List<string> Included, List<SqlDatabaseRunResult> Skipped) Select(
        IEnumerable<SqlDatabaseInfo> databases, SqlServerSource source, SqlBackupType type)
    {
        var all = databases.ToList();
        var excludes = source.ExcludePatterns.Select(WildcardToRegex).ToList();
        var included = new List<string>();
        var skipped = new List<SqlDatabaseRunResult>();

        foreach (var db in all)
        {
            var wanted = source.AllUserDatabases
                ? !db.IsSystem
                : source.Databases.Contains(db.Name, StringComparer.OrdinalIgnoreCase);
            if (!wanted || excludes.Any(r => r.IsMatch(db.Name))) continue;

            var reason = SkipReason(db, type);
            if (reason is null) included.Add(db.Name);
            else skipped.Add(new SqlDatabaseRunResult { Database = db.Name, BackupType = type, Status = "SKIPPED", Message = reason });
        }

        if (!source.AllUserDatabases)
        {
            var onServer = all.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            skipped.AddRange(source.Databases.Where(n => !onServer.Contains(n)).Select(n =>
                new SqlDatabaseRunResult { Database = n, BackupType = type, Status = "FAILED", Message = "Database not found on server" }));
        }

        return (included, skipped);
    }

    private static string? SkipReason(SqlDatabaseInfo db, SqlBackupType type)
    {
        if (!db.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)) return $"Database is {db.State}";
        if (type == SqlBackupType.Log && db.RecoveryModel.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
            return "SIMPLE recovery model has no log backups";
        if (type == SqlBackupType.Diff && db.Name.Equals("master", StringComparison.OrdinalIgnoreCase))
            return "master supports full backups only";
        return null;
    }

    private static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
}
