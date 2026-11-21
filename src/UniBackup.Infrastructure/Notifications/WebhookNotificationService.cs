using System.Text;
using System.Text.Json;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Infrastructure.Notifications;

public class WebhookNotificationService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<Result> SendWebhookAlertAsync(AppConfig config, BackupJob job, RunHistoryRecord history, CancellationToken ct = default)
    {
        if (!config.Webhooks.Enabled) return Result.Success();

        var isSuccess = history.Status == "SUCCESS";
        if (isSuccess && !config.Webhooks.NotifyOnSuccess) return Result.Success();
        if (!isSuccess && !config.Webhooks.NotifyOnFailure) return Result.Success();

        var server = ServerIdentity.Current;
        var statusEmoji = isSuccess ? "✅" : (history.Status == "PARTIAL" ? "⚠️" : "❌");

        var payloadText = $"{statusEmoji} *UniBackup Alert* from *{server.DisplayLabel}*\n" +
                          $"• *Job:* {job.Name}\n" +
                          $"• *Status:* {history.Status}\n" +
                          $"• *Size:* {FormattingUtils.FormatBytes(history.ArchiveSizeBytes)}\n" +
                          $"• *Duration:* {FormattingUtils.FormatDuration(TimeSpan.FromSeconds(history.DurationSeconds))}\n" +
                          (string.IsNullOrEmpty(history.ErrorMessage) ? "" : $"• *Error:* {history.ErrorMessage}\n");

        // 1. Teams Webhook (MessageCard format)
        if (!string.IsNullOrWhiteSpace(config.Webhooks.TeamsWebhookUrl))
        {
            await SendJsonAsync(config.Webhooks.TeamsWebhookUrl, new
            {
                text = payloadText
            }, ct);
        }

        // 2. Slack Webhook
        if (!string.IsNullOrWhiteSpace(config.Webhooks.SlackWebhookUrl))
        {
            await SendJsonAsync(config.Webhooks.SlackWebhookUrl, new
            {
                text = payloadText
            }, ct);
        }

        // 3. Discord Webhook
        if (!string.IsNullOrWhiteSpace(config.Webhooks.DiscordWebhookUrl))
        {
            await SendJsonAsync(config.Webhooks.DiscordWebhookUrl, new
            {
                content = payloadText
            }, ct);
        }

        return Result.Success();
    }

    private static async Task SendJsonAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispatch webhook to {Url}: {Error}", url, ex.Message);
        }
    }
}
