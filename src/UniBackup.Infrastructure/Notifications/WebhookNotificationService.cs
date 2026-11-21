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

        // 4. Telegram Bot API
        if (!string.IsNullOrWhiteSpace(config.Webhooks.TelegramBotToken) && !string.IsNullOrWhiteSpace(config.Webhooks.TelegramChatId))
        {
            var telegramUrl = $"https://api.telegram.org/bot{config.Webhooks.TelegramBotToken.Trim()}/sendMessage";
            await SendJsonAsync(telegramUrl, new
            {
                chat_id = config.Webhooks.TelegramChatId.Trim(),
                text = payloadText,
                parse_mode = "Markdown"
            }, ct);
        }

        return Result.Success();
    }

    public async Task<Result> SendTestSlackAsync(string webhookUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return Result.Failure("Slack webhook URL is empty.");
        var server = ServerIdentity.Current;
        var text = $"🔔 *UniBackup Slack Test Alert*\nConnection to *{server.DisplayLabel}* was successful at {DateTime.Now:yyyy-MM-dd HH:mm:ss}!";
        return await SendJsonWithResultAsync(webhookUrl, new { text }, ct);
    }

    public async Task<Result> SendTestDiscordAsync(string webhookUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return Result.Failure("Discord webhook URL is empty.");
        var server = ServerIdentity.Current;
        var content = $"🔔 **UniBackup Discord Test Alert**\nConnection to **{server.DisplayLabel}** was successful at {DateTime.Now:yyyy-MM-dd HH:mm:ss}!";
        return await SendJsonWithResultAsync(webhookUrl, new { content }, ct);
    }

    public async Task<Result> SendTestTeamsAsync(string webhookUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return Result.Failure("Microsoft Teams webhook URL is empty.");
        var server = ServerIdentity.Current;
        var text = $"🔔 *UniBackup Teams Test Alert*\nConnection to *{server.DisplayLabel}* was successful at {DateTime.Now:yyyy-MM-dd HH:mm:ss}!";
        return await SendJsonWithResultAsync(webhookUrl, new { text }, ct);
    }

    public async Task<Result> SendTestTelegramAsync(string botToken, string chatId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botToken)) return Result.Failure("Telegram Bot Token is empty.");
        if (string.IsNullOrWhiteSpace(chatId)) return Result.Failure("Telegram Chat ID is empty.");
        var server = ServerIdentity.Current;
        var telegramUrl = $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage";
        var text = $"🔔 *UniBackup Telegram Test Alert*\nConnection to *{server.DisplayLabel}* was successful at {DateTime.Now:yyyy-MM-dd HH:mm:ss}!";
        return await SendJsonWithResultAsync(telegramUrl, new
        {
            chat_id = chatId.Trim(),
            text,
            parse_mode = "Markdown"
        }, ct);
    }

    private static async Task<Result> SendJsonWithResultAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"HTTP {(int)response.StatusCode}: {err}");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Webhook failed: {ex.Message}");
        }
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
