using System.Text;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Serilog;

namespace UniBackup.Infrastructure.Notifications;

public class EmailNotificationService : INotificationService
{
    public async Task<Result> SendRunReportAsync(AppConfig config, BackupJob job, RunHistoryRecord history, CancellationToken ct = default)
    {
        if (!config.Email.Enabled) return Result.Success();

        var isSuccess = history.Status == "SUCCESS";
        if (isSuccess && !config.Email.NotifyOnSuccess) return Result.Success();
        if (!isSuccess && !config.Email.NotifyOnFailure) return Result.Success();

        var server = ServerIdentity.Current;
        var statusEmoji = isSuccess ? "✔ SUCCESS" : (history.Status == "PARTIAL" ? "⚠ PARTIAL" : "✖ FAILED");
        var subject = $"[{server.DisplayLabel}] Backup {statusEmoji}: {job.Name}";

        var html = BuildSingleJobHtml(server, job, history);
        return await SendEmailInternalAsync(config, subject, html, ct);
    }

    public async Task<Result> SendBatchReportAsync(AppConfig config, List<RunHistoryRecord> histories, TimeSpan totalDuration, CancellationToken ct = default)
    {
        if (!config.Email.Enabled || histories.Count == 0) return Result.Success();

        var failedCount = histories.Count(h => h.Status == "FAILED");
        var partialCount = histories.Count(h => h.Status == "PARTIAL");
        var successCount = histories.Count(h => h.Status == "SUCCESS");
        var isAllSuccess = failedCount == 0 && partialCount == 0;

        if (isAllSuccess && !config.Email.NotifyOnSuccess) return Result.Success();
        if (!isAllSuccess && !config.Email.NotifyOnFailure) return Result.Success();

        var server = ServerIdentity.Current;
        var statusTag = isAllSuccess ? "✔ ALL SUCCESS" : $"✖ {failedCount + partialCount} ISSUES";
        var subject = $"[{server.DisplayLabel}] Nightly Backup Summary: {statusTag} ({histories.Count} jobs)";

        var html = BuildBatchHtml(server, histories, totalDuration);
        return await SendEmailInternalAsync(config, subject, html, ct);
    }

    public async Task<Result> SendTestNotificationAsync(AppConfig config, CancellationToken ct = default)
    {
        var server = ServerIdentity.Current;
        var subject = $"[{server.DisplayLabel}] UniBackup Test Notification";
        var html = $@"
<div style='font-family: Segoe UI, sans-serif; padding: 20px; background: #f8fafc; border-radius: 8px;'>
    <h2 style='color: #0f172a; margin-top: 0;'>✔ UniBackup Notification Test</h2>
    <p>This is a test notification from server: <strong>{server.DisplayLabel}</strong></p>
    <p>Time: <strong>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</strong></p>
    <p style='color: #10b981; font-weight: bold;'>SMTP configuration and authentication verified successfully!</p>
</div>";

        return await SendEmailInternalAsync(config, subject, html, ct);
    }

    private async Task<Result> SendEmailInternalAsync(AppConfig config, string subject, string htmlBody, CancellationToken ct)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("UniBackup", config.Email.FromAddress));

            foreach (var to in config.Email.ToAddresses.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                message.To.Add(MailboxAddress.Parse(to.Trim()));
            }

            message.Subject = subject;

            var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            client.Timeout = 30000;
            // Allow servers using shared hosting / proxy certificates
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;

            var secureSocket = config.Email.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(config.Email.SmtpHost, config.Email.SmtpPort, secureSocket, ct);

            // In modern setups where username input is removed from UI, authentication username defaults to FromAddress
            var username = !string.IsNullOrWhiteSpace(config.Email.FromAddress) ? config.Email.FromAddress : config.Email.Username;
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(config.Email.Password))
            {
                await client.AuthenticateAsync(username, config.Email.Password, ct);
            }

            await client.SendAsync(FormatOptions.Default, message, ct);
            await client.DisconnectAsync(true, ct);

            Log.Information("Email alert sent successfully: {Subject}", subject);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send email alert: {Error}", ex.Message);
            return Result.Failure($"Email dispatch error: {ex.Message}");
        }
    }

    private string BuildSingleJobHtml(ServerIdentity server, BackupJob job, RunHistoryRecord history)
    {
        var statusColor = history.Status == "SUCCESS" ? "#10b981" : (history.Status == "PARTIAL" ? "#f59e0b" : "#ef4444");
        var tempFree = FormattingUtils.FormatBytes(AppPaths.GetFreeDiskSpaceBytes(AppPaths.DefaultTempDirectory));

        var sb = new StringBuilder();
        sb.Append($@"
<div style='font-family: Segoe UI, sans-serif; max-width: 650px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden;'>
  <div style='background: {statusColor}; color: white; padding: 18px 24px;'>
    <h2 style='margin: 0; font-size: 20px;'>UniBackup Backup Report</h2>
    <div style='font-size: 13px; opacity: 0.9;'>Server: {server.DisplayLabel}</div>
  </div>
  <div style='padding: 24px; background: white;'>
    <table style='width: 100%; border-collapse: collapse; font-size: 14px;'>
      <tr><td style='padding: 8px 0; color: #64748b; width: 140px;'>Job Name:</td><td style='font-weight: 600;'>{job.Name}</td></tr>
      <tr><td style='padding: 8px 0; color: #64748b;'>Status:</td><td><span style='background: {statusColor}; color: white; padding: 2px 8px; border-radius: 4px; font-weight: bold;'>{history.Status}</span></td></tr>
      <tr><td style='padding: 8px 0; color: #64748b;'>Source Path:</td><td style='font-family: Consolas, monospace;'>{job.EffectiveSourcePath}</td></tr>
      <tr><td style='padding: 8px 0; color: #64748b;'>Archive Size:</td><td>{FormattingUtils.FormatBytes(history.ArchiveSizeBytes)}</td></tr>
      <tr><td style='padding: 8px 0; color: #64748b;'>Duration:</td><td>{FormattingUtils.FormatDuration(TimeSpan.FromSeconds(history.DurationSeconds))}</td></tr>
      <tr><td style='padding: 8px 0; color: #64748b;'>Temp Free Space:</td><td>{tempFree}</td></tr>
    </table>
    <h3 style='margin-top: 24px; margin-bottom: 12px; font-size: 15px; border-bottom: 1px solid #e2e8f0; padding-bottom: 6px;'>Destinations</h3>
    <table style='width: 100%; border-collapse: collapse; font-size: 13px;'>
      <thead>
        <tr style='background: #f1f5f9; text-align: left;'>
          <th style='padding: 8px;'>Target</th>
          <th style='padding: 8px;'>Location</th>
          <th style='padding: 8px;'>Status</th>
        </tr>
      </thead>
      <tbody>");

        foreach (var dest in history.Destinations)
        {
            var destColor = dest.Status == "SUCCESS" ? "#10b981" : "#ef4444";
            sb.Append($@"
        <tr style='border-bottom: 1px solid #f1f5f9;'>
          <td style='padding: 8px; font-weight: 600;'>{dest.Destination.ToUpperInvariant()}</td>
          <td style='padding: 8px; font-family: Consolas, monospace;'>{dest.TargetLocation}</td>
          <td style='padding: 8px; color: {destColor}; font-weight: bold;'>{dest.Status}</td>
        </tr>");
        }

        if (!string.IsNullOrEmpty(history.ErrorMessage))
        {
            sb.Append($@"
    <div style='margin-top: 16px; padding: 12px; background: #fee2e2; border-left: 4px solid #ef4444; color: #991b1b; font-size: 13px;'>
      <strong>Error:</strong> {history.ErrorMessage}
    </div>");
        }

        sb.Append(@"
      </tbody>
    </table>
  </div>
</div>");

        return sb.ToString();
    }

    private string BuildBatchHtml(ServerIdentity server, List<RunHistoryRecord> histories, TimeSpan totalDuration)
    {
        var totalBytes = histories.Sum(h => h.ArchiveSizeBytes);
        var failed = histories.Count(h => h.Status == "FAILED");
        var tempFree = FormattingUtils.FormatBytes(AppPaths.GetFreeDiskSpaceBytes(AppPaths.DefaultTempDirectory));

        var sb = new StringBuilder();
        sb.Append($@"
<div style='font-family: Segoe UI, sans-serif; max-width: 700px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden;'>
  <div style='background: {(failed > 0 ? "#ef4444" : "#10b981")}; color: white; padding: 18px 24px;'>
    <h2 style='margin: 0; font-size: 20px;'>UniBackup Nightly Summary</h2>
    <div style='font-size: 13px; opacity: 0.9;'>Server: {server.DisplayLabel} · Duration: {FormattingUtils.FormatDuration(totalDuration)} · Total Size: {FormattingUtils.FormatBytes(totalBytes)}</div>
  </div>
  <div style='padding: 24px; background: white;'>
    <p>Temp Drive Free: <strong>{tempFree}</strong></p>
    <table style='width: 100%; border-collapse: collapse; font-size: 13px;'>
      <thead>
        <tr style='background: #f1f5f9; text-align: left;'>
          <th style='padding: 8px;'>Job Name</th>
          <th style='padding: 8px;'>Size</th>
          <th style='padding: 8px;'>Time</th>
          <th style='padding: 8px;'>Status</th>
        </tr>
      </thead>
      <tbody>");

        foreach (var h in histories)
        {
            var color = h.Status == "SUCCESS" ? "#10b981" : (h.Status == "PARTIAL" ? "#f59e0b" : "#ef4444");
            sb.Append($@"
        <tr style='border-bottom: 1px solid #f1f5f9;'>
          <td style='padding: 8px; font-weight: 600;'>{h.JobName}</td>
          <td style='padding: 8px;'>{FormattingUtils.FormatBytes(h.ArchiveSizeBytes)}</td>
          <td style='padding: 8px;'>{FormattingUtils.FormatDuration(TimeSpan.FromSeconds(h.DurationSeconds))}</td>
          <td style='padding: 8px; color: {color}; font-weight: bold;'>{h.Status}</td>
        </tr>");
        }

        sb.Append(@"
      </tbody>
    </table>
  </div>
</div>");

        return sb.ToString();
    }
}
