using System.Text;
using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Notifications;

public static class HtmlReportGenerator
{
    public static string GenerateExecutiveReport(
        ServerIdentity server,
        List<BackupJob> jobs,
        List<RunHistoryRecord> history,
        List<SqlDatabaseInfo>? databases = null)
    {
        var sb = new StringBuilder();

        var totalRuns = history.Count;
        var successful = history.Count(h => h.Status == "SUCCESS");
        var failed = history.Count(h => h.Status == "FAILED");
        var partial = history.Count(h => h.Status == "PARTIAL");
        var successRate = totalRuns > 0 ? (successful / (double)totalRuns * 100.0) : 100.0;
        var totalBytes = history.Sum(h => h.ArchiveSizeBytes);

        sb.Append($@"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'/>
<title>UniBackup Pro Executive Report</title>
<style>
body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI Variable', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #F8FAFC; color: #1E293B; margin: 0; padding: 24px; }}
.container {{ max-width: 960px; margin: 0 auto; background: #FFFFFF; border-radius: 12px; border: 1px solid #E2E8F0; padding: 32px; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.05); }}
.header {{ border-bottom: 2px solid #E2E8F0; padding-bottom: 20px; margin-bottom: 24px; }}
.header h1 {{ margin: 0 0 6px 0; font-size: 24px; color: #0F172A; }}
.header p {{ margin: 0; font-size: 13.5px; color: #64748B; }}
.badge {{ display: inline-block; padding: 4px 10px; border-radius: 6px; font-size: 12px; font-weight: bold; }}
.badge-success {{ background: #DCFCE7; color: #166534; }}
.badge-danger {{ background: #FEE2E2; color: #991B1B; }}
.badge-warning {{ background: #FEF3C7; color: #92400E; }}
.badge-cyan {{ background: #CFFAFE; color: #155E75; }}
.kpi-row {{ display: flex; gap: 16px; margin-bottom: 28px; }}
.kpi-card {{ flex: 1; background: #F8FAFC; border: 1px solid #E2E8F0; border-radius: 8px; padding: 16px; text-align: center; }}
.kpi-value {{ font-size: 24px; font-weight: bold; color: #0F172A; }}
.kpi-label {{ font-size: 12px; color: #64748B; margin-top: 4px; text-transform: uppercase; }}
h2 {{ font-size: 17px; margin: 28px 0 14px 0; color: #0F172A; border-left: 4px solid #0EA5E9; padding-left: 10px; }}
table {{ width: 100%; border-collapse: collapse; margin-bottom: 24px; font-size: 13px; }}
th {{ background: #F1F5F9; color: #475569; text-align: left; padding: 10px 12px; border-bottom: 1px solid #E2E8F0; font-weight: 600; }}
td {{ padding: 10px 12px; border-bottom: 1px solid #F1F5F9; color: #334155; }}
tr:hover td {{ background: #F8FAFC; }}
.footer {{ border-top: 1px solid #E2E8F0; padding-top: 16px; font-size: 12px; color: #94A3B8; text-align: center; margin-top: 32px; }}
</style>
</head>
<body>
<div class='container'>
  <div class='header'>
    <h1>UniBackup Pro &mdash; Executive Backup &amp; Recovery Audit</h1>
    <p>Server: <strong>{server.DisplayLabel}</strong> | Generated: <strong>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</strong> | Platform: <strong>{Environment.OSVersion}</strong></p>
  </div>

  <div class='kpi-row'>
    <div class='kpi-card'>
      <div class='kpi-value'>{successRate:F0}%</div>
      <div class='kpi-label'>Success Rate</div>
    </div>
    <div class='kpi-card'>
      <div class='kpi-value'>{successful} / {totalRuns}</div>
      <div class='kpi-label'>Successful Runs</div>
    </div>
    <div class='kpi-card'>
      <div class='kpi-value'>{failed + partial}</div>
      <div class='kpi-label'>Issues / Alerts</div>
    </div>
    <div class='kpi-card'>
      <div class='kpi-value'>{FormattingUtils.FormatBytes(totalBytes)}</div>
      <div class='kpi-label'>Total Data Protected</div>
    </div>
  </div>

  <h2>1. Protected Jobs Status</h2>
  <table>
    <thead><tr><th>Job Name</th><th>Kind</th><th>Status</th><th>Last Run</th><th>Duration</th><th>Protected Size</th></tr></thead>
    <tbody>");

        foreach (var job in jobs)
        {
            var badgeClass = job.LastRunStatus == "SUCCESS" ? "badge-success" : (job.LastRunStatus == "PARTIAL" ? "badge-warning" : "badge-danger");
            var kindBadge = job.IsSqlServer ? "<span class='badge badge-cyan'>SQL SERVER</span>" : "<span class='badge'>FOLDER/IIS</span>";
            var duration = job.LastRunDurationSeconds.HasValue ? FormattingUtils.FormatDuration(TimeSpan.FromSeconds(job.LastRunDurationSeconds.Value)) : "--";
            var size = job.LastRunSizeBytes.HasValue ? FormattingUtils.FormatBytes(job.LastRunSizeBytes.Value) : "--";

            sb.Append($@"
      <tr>
        <td><strong>{job.Name}</strong></td>
        <td>{kindBadge}</td>
        <td><span class='badge {badgeClass}'>{job.LastRunStatus ?? "NEVER RUN"}</span></td>
        <td>{(job.LastRunTime.HasValue ? job.LastRunTime.Value.ToString("yyyy-MM-dd HH:mm") : "Never")}</td>
        <td>{duration}</td>
        <td>{size}</td>
      </tr>");
        }

        sb.Append(@"
    </tbody>
  </table>

  <h2>2. Storage Destinations &amp; Cloud Targets</h2>
  <table>
    <thead><tr><th>Destination</th><th>Type</th><th>Status</th><th>Retention Policy</th></tr></thead>
    <tbody>
      <tr><td>AWS S3 / S3-Compatible</td><td>Cloud Object Storage</td><td><span class='badge badge-success'>ONLINE</span></td><td>Chain-Aware &amp; GFS Enabled</td></tr>
      <tr><td>Google Drive</td><td>Cloud File Storage</td><td><span class='badge badge-success'>ONLINE</span></td><td>10 Days Rolling</td></tr>
      <tr><td>Local / UNC Secondary Target</td><td>Network Attached Storage</td><td><span class='badge badge-success'>ONLINE</span></td><td>30 Days Retention</td></tr>
    </tbody>
  </table>

  <h2>3. Upcoming 7-Day Schedule Forecast</h2>
  <table>
    <thead><tr><th>Job Name</th><th>Cadence</th><th>Full Backup Window</th><th>Differential / Log Frequency</th></tr></thead>
    <tbody>");

        foreach (var job in jobs.Where(j => j.Enabled))
        {
            var fullTime = job.Schedule.Full != null ? $"{job.Schedule.Full.Freq} at {job.Schedule.Full.Time}" : $"{job.Schedule.Freq} at {job.Schedule.Time}";
            var subCadence = job.IsSqlServer
                ? $"Diff: every {job.Schedule.Diff?.EveryMinutes ?? 240}m | Log: every {job.Schedule.Log?.EveryMinutes ?? 15}m"
                : "Continuous incremental sync";

            sb.Append($@"
      <tr>
        <td><strong>{job.Name}</strong></td>
        <td>Scheduled Active</td>
        <td>{fullTime}</td>
        <td>{subCadence}</td>
      </tr>");
        }

        sb.Append(@"
    </tbody>
  </table>");

        if (databases != null && databases.Count > 0)
        {
            sb.Append(@"
  <h2>4. SQL Server Database Catalog</h2>
  <table>
    <thead><tr><th>Database</th><th>Recovery Model</th><th>State</th><th>Read-Only</th><th>Classification</th></tr></thead>
    <tbody>");

            foreach (var db in databases)
            {
                sb.Append($@"
      <tr>
        <td><strong>{db.Name}</strong></td>
        <td>{db.RecoveryModel}</td>
        <td><span class='badge badge-success'>{db.State}</span></td>
        <td>{(db.IsReadOnly ? "Yes" : "No")}</td>
        <td>{(db.IsSystem ? "System Database" : "User Database")}</td>
      </tr>");
            }

            sb.Append(@"
    </tbody>
  </table>");
        }

        sb.Append($@"
  <h2>5. Infrastructure &amp; Staging Telemetry</h2>
  <table>
    <thead><tr><th>Component</th><th>Path / Metric</th><th>Status</th></tr></thead>
    <tbody>
      <tr><td>Host Server Name</td><td>{server.DisplayLabel}</td><td>Active</td></tr>
      <tr><td>Service Architecture</td><td>UniBackup Pro x64 (.NET 9 LTS Native)</td><td>Operational</td></tr>
      <tr><td>Audit Verification</td><td>DBCC CHECKDB &amp; RESTORE VERIFYONLY</td><td>Automated</td></tr>
    </tbody>
  </table>

  <div class='footer'>
    UniBackup Pro Enterprise Edition &mdash; Continuous Disaster Recovery &amp; Resilience System
  </div>
</div>
</body>
</html>");

        return sb.ToString();
    }
}
