using System.Text;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Monitoring;

public static class PrometheusMetricsService
{
    public static string GenerateMetricsText(List<BackupJob> jobs, List<RunHistoryRecord> history)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# HELP unibackup_job_last_status Last run status of the backup job (1 for SUCCESS, 0 for FAILED/PARTIAL)");
        sb.AppendLine("# TYPE unibackup_job_last_status gauge");
        foreach (var job in jobs)
        {
            var val = job.LastRunStatus == "SUCCESS" ? 1 : 0;
            sb.AppendLine($"unibackup_job_last_status{{job=\"{Sanitize(job.Name)}\",kind=\"{job.Kind}\"}} {val}");
        }

        sb.AppendLine();
        sb.AppendLine("# HELP unibackup_job_last_duration_seconds Last run duration in seconds");
        sb.AppendLine("# TYPE unibackup_job_last_duration_seconds gauge");
        foreach (var job in jobs)
        {
            var val = job.LastRunDurationSeconds ?? 0;
            sb.AppendLine($"unibackup_job_last_duration_seconds{{job=\"{Sanitize(job.Name)}\"}} {val:F1}");
        }

        sb.AppendLine();
        sb.AppendLine("# HELP unibackup_job_last_size_bytes Last run archive size in bytes");
        sb.AppendLine("# TYPE unibackup_job_last_size_bytes gauge");
        foreach (var job in jobs)
        {
            var val = job.LastRunSizeBytes ?? 0;
            sb.AppendLine($"unibackup_job_last_size_bytes{{job=\"{Sanitize(job.Name)}\"}} {val}");
        }

        sb.AppendLine();
        sb.AppendLine("# HELP unibackup_total_runs_total Total recorded backup runs by status");
        sb.AppendLine("# TYPE unibackup_total_runs_total counter");
        var success = history.Count(h => h.Status == "SUCCESS");
        var partial = history.Count(h => h.Status == "PARTIAL");
        var failed = history.Count(h => h.Status == "FAILED");
        sb.AppendLine($"unibackup_total_runs_total{{status=\"SUCCESS\"}} {success}");
        sb.AppendLine($"unibackup_total_runs_total{{status=\"PARTIAL\"}} {partial}");
        sb.AppendLine($"unibackup_total_runs_total{{status=\"FAILED\"}} {failed}");

        return sb.ToString();
    }

    private static string Sanitize(string val) =>
        val.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "");
}
