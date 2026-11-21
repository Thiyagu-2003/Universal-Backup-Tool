using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class SqlRestorePlanner
{
    private record ParsedSqlArchive(CloudArchiveItem Item, string Database, SqlBackupType Type, DateTime Timestamp);

    public static Result<SqlRestorePlan> Plan(IEnumerable<CloudArchiveItem> items, string database, DateTime? targetTime)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var matching = new List<ParsedSqlArchive>();
        foreach (var item in items)
        {
            if (SqlBackupFileName.TryParse(item.DisplayName, out var parsed))
            {
                if (string.Equals(parsed.Database, database, StringComparison.OrdinalIgnoreCase))
                {
                    matching.Add(new ParsedSqlArchive(item, parsed.Database, parsed.Type, parsed.Timestamp));
                }
            }
        }

        if (matching.Count == 0)
        {
            return Result<SqlRestorePlan>.Failure($"No backups found for database '{database}'.");
        }

        var effectiveTarget = targetTime ?? DateTime.MaxValue;

        // 1. Find latest FULL backup at or before targetTime
        var eligibleFulls = matching
            .Where(m => m.Type == SqlBackupType.Full && m.Timestamp <= effectiveTarget)
            .OrderBy(m => m.Timestamp)
            .ToList();

        if (eligibleFulls.Count == 0)
        {
            return Result<SqlRestorePlan>.Failure(
                targetTime.HasValue
                    ? $"No FULL backup found for database '{database}' at or before {targetTime:yyyy-MM-dd HH:mm:ss}."
                    : $"No FULL backup found for database '{database}'.");
        }

        var chosenFull = eligibleFulls.Last();

        // 2. Find newest DIFF backup between chosen FULL and targetTime (if any)
        var eligibleDiffs = matching
            .Where(m => m.Type == SqlBackupType.Diff && m.Timestamp > chosenFull.Timestamp && m.Timestamp <= effectiveTarget)
            .OrderBy(m => m.Timestamp)
            .ToList();

        var chosenDiff = eligibleDiffs.LastOrDefault();

        // 3. Find LOG backups covering after the base (DIFF if present, else FULL)
        var baseTimestamp = chosenDiff?.Timestamp ?? chosenFull.Timestamp;
        var availableLogs = matching
            .Where(m => m.Type == SqlBackupType.Log && m.Timestamp > baseTimestamp)
            .OrderBy(m => m.Timestamp)
            .ToList();

        var chosenLogs = new List<CloudArchiveItem>();
        foreach (var log in availableLogs)
        {
            chosenLogs.Add(log.Item);
            if (log.Timestamp >= effectiveTarget)
            {
                // This log encompasses the target point in time
                break;
            }
        }

        var plan = new SqlRestorePlan
        {
            Database = database,
            TargetTime = targetTime,
            FullBackup = chosenFull.Item,
            DiffBackup = chosenDiff?.Item,
            LogBackups = chosenLogs
        };

        return Result<SqlRestorePlan>.Success(plan);
    }
}
