using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Notifications;

public static class NotificationTemplateExpander
{
    public static string Expand(
        string template,
        BackupJob job,
        RunHistoryRecord history,
        SqlBackupType? type = null)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var typeStr = type?.ToString().ToUpperInvariant() ?? "FULL";
        var databases = history.SqlDatabases.Count > 0
            ? string.Join(", ", history.SqlDatabases.Select(d => d.Database))
            : (job.IsSqlServer ? string.Join(", ", job.Sql?.Databases ?? []) : Path.GetFileName(job.EffectiveSourcePath));

        var durationStr = FormattingUtils.FormatDuration(TimeSpan.FromSeconds(history.DurationSeconds));
        var sizeStr = FormattingUtils.FormatBytes(history.ArchiveSizeBytes);
        var errors = string.IsNullOrWhiteSpace(history.ErrorMessage) ? "None" : history.ErrorMessage;

        return template
            .Replace("{job}", job.Name)
            .Replace("{status}", history.Status)
            .Replace("{type}", typeStr)
            .Replace("{databases}", databases)
            .Replace("{duration}", durationStr)
            .Replace("{size}", sizeStr)
            .Replace("{server}", ServerIdentity.Current.DisplayLabel)
            .Replace("{errors}", errors);
    }
}
