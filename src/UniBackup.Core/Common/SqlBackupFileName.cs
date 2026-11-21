using System.Globalization;
using System.Text.RegularExpressions;
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static partial class SqlBackupFileName
{
    public static string Format(string database, SqlBackupType type, DateTime timestamp) =>
        $"{FormattingUtils.SanitizePathSegment(database)}_{Tag(type)}_{timestamp:yyyyMMdd_HHmmss}{(type == SqlBackupType.Log ? ".trn" : ".bak")}";

    public static string RemoteSubfolder(string database, SqlBackupType type) =>
        $"{FormattingUtils.SanitizePathSegment(database)}/{Tag(type)}";

    public static bool TryParse(string fileName, out (string Database, SqlBackupType Type, DateTime Timestamp) parsed)
    {
        parsed = default;
        var m = Pattern().Match(fileName);
        if (!m.Success) return false;

        var type = m.Groups["type"].Value switch
        {
            "FULL" => SqlBackupType.Full,
            "DIFF" => SqlBackupType.Diff,
            _ => SqlBackupType.Log
        };
        var ts = DateTime.ParseExact(m.Groups["ts"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        parsed = (m.Groups["db"].Value, type, ts);
        return true;
    }

    private static string Tag(SqlBackupType type) => type switch
    {
        SqlBackupType.Full => "FULL",
        SqlBackupType.Diff => "DIFF",
        _ => "LOG"
    };

    [GeneratedRegex(@"^(?<db>.+)_(?<type>FULL|DIFF|LOG)_(?<ts>\d{8}_\d{6})\.(bak|trn)(\.ubenc)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
