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

        // 5. Pushover Mobile Push (iOS & Android)
        if (!string.IsNullOrWhiteSpace(config.Webhooks.PushoverUserKey) && !string.IsNullOrWhiteSpace(config.Webhooks.PushoverApiToken))
        {
            await DispatchPushoverAsync(
                config.Webhooks.PushoverUserKey.Trim(),
                config.Webhooks.PushoverApiToken.Trim(),
                $"{statusEmoji} UniBackup: {job.Name}",
                payloadText,
                isSuccess ? 0 : 1,
                ct);
        }

        // 6. ntfy.sh (Pub/Sub Push)
        if (!string.IsNullOrWhiteSpace(config.Webhooks.NtfyTopic))
        {
            var serverUrl = string.IsNullOrWhiteSpace(config.Webhooks.NtfyServerUrl) ? "https://ntfy.sh" : config.Webhooks.NtfyServerUrl;
            await DispatchNtfyAsync(
                serverUrl,
                config.Webhooks.NtfyTopic.Trim(),
                config.Webhooks.NtfyToken,
                $"{statusEmoji} UniBackup: {job.Name} ({history.Status})",
                payloadText,
                isSuccess ? "default" : "urgent",
                isSuccess ? "white_check_mark" : "warning,rotating_light",
                ct);
        }

        // 7. PagerDuty Events API v2 (DevOps On-Call)
        if (!string.IsNullOrWhiteSpace(config.Webhooks.PagerDutyRoutingKey))
        {
            await DispatchPagerDutyAsync(
                config.Webhooks.PagerDutyRoutingKey.Trim(),
                $"{statusEmoji} UniBackup [{server.DisplayLabel}]: {job.Name} - {history.Status}",
                server.DisplayLabel,
                isSuccess ? "info" : "error",
                new
                {
                    job = job.Name,
                    status = history.Status,
                    size = FormattingUtils.FormatBytes(history.ArchiveSizeBytes),
                    duration = FormattingUtils.FormatDuration(TimeSpan.FromSeconds(history.DurationSeconds)),
                    error = history.ErrorMessage
                },
                ct);
        }

        // 8. Generic Custom Webhook (n8n, Zapier, Make, Home Assistant, Splunk, Datadog)
        if (!string.IsNullOrWhiteSpace(config.Webhooks.CustomWebhookUrl))
        {
            await DispatchCustomWebhookAsync(
                config.Webhooks.CustomWebhookUrl.Trim(),
                config.Webhooks.CustomWebhookHeaderName,
                config.Webhooks.CustomWebhookHeaderValue,
                new
                {
                    @event = "backup_completed",
                    server = server.DisplayLabel,
                    job = job.Name,
                    status = history.Status,
                    archive_size_bytes = history.ArchiveSizeBytes,
                    duration_seconds = history.DurationSeconds,
                    error = history.ErrorMessage,
                    timestamp = DateTime.UtcNow.ToString("o")
                },
                ct);
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

    public async Task<Result> SendTestPushoverAsync(string userKey, string apiToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userKey)) return Result.Failure("Pushover User Key is required.");
        if (string.IsNullOrWhiteSpace(apiToken)) return Result.Failure("Pushover API Token is required.");
        var server = ServerIdentity.Current;
        return await DispatchPushoverAsync(
            userKey.Trim(),
            apiToken.Trim(),
            "🔔 UniBackup Pushover Test",
            $"UniBackup mobile push test from {server.DisplayLabel} successful at {DateTime.Now:yyyy-MM-dd HH:mm:ss}!",
            0,
            ct);
    }

    public async Task<Result> SendTestNtfyAsync(string serverUrl, string topic, string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return Result.Failure("ntfy Topic is required.");
        var sUrl = string.IsNullOrWhiteSpace(serverUrl) ? "https://ntfy.sh" : serverUrl;
        var server = ServerIdentity.Current;
        return await DispatchNtfyAsync(
            sUrl,
            topic.Trim(),
            token,
            "🔔 UniBackup ntfy Test",
            $"UniBackup push notification test from {server.DisplayLabel} successful at {DateTime.Now:yyyy-MM-dd HH:mm:ss}!",
            "default",
            "white_check_mark,bell",
            ct);
    }

    public async Task<Result> SendTestPagerDutyAsync(string routingKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(routingKey)) return Result.Failure("PagerDuty Integration / Routing Key is required.");
        var server = ServerIdentity.Current;
        return await DispatchPagerDutyAsync(
            routingKey.Trim(),
            $"UniBackup Test Incident from {server.DisplayLabel}",
            server.DisplayLabel,
            "info",
            new { message = "UniBackup PagerDuty integration test executed successfully." },
            ct);
    }

    public async Task<Result> SendTestCustomWebhookAsync(string url, string? headerName, string? headerValue, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Result.Failure("Custom Webhook URL is required.");
        var server = ServerIdentity.Current;
        return await DispatchCustomWebhookAsync(
            url.Trim(),
            headerName,
            headerValue,
            new
            {
                @event = "test_alert",
                server = server.DisplayLabel,
                message = "UniBackup Generic Custom Webhook connection test successful!",
                timestamp = DateTime.UtcNow.ToString("o")
            },
            ct);
    }

    private static async Task<Result> DispatchPushoverAsync(string userKey, string apiToken, string title, string message, int priority, CancellationToken ct)
    {
        try
        {
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("token", apiToken),
                new("user", userKey),
                new("title", title),
                new("message", message),
                new("priority", priority.ToString())
            };
            using var content = new FormUrlEncodedContent(pairs);
            var response = await HttpClient.PostAsync("https://api.pushover.net/1/messages.json", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"Pushover HTTP {(int)response.StatusCode}: {err}");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispatch Pushover alert: {Error}", ex.Message);
            return Result.Failure($"Pushover error: {ex.Message}");
        }
    }

    private static async Task<Result> DispatchNtfyAsync(string serverUrl, string topic, string? token, string title, string message, string priority, string tags, CancellationToken ct)
    {
        try
        {
            var endpoint = $"{serverUrl.TrimEnd('/')}/{topic.TrimStart('/')}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(message, Encoding.UTF8, "text/plain")
            };
            request.Headers.Add("Title", title);
            request.Headers.Add("Priority", priority);
            request.Headers.Add("Tags", tags);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Add("Authorization", $"Bearer {token.Trim()}");
            }

            var response = await HttpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"ntfy HTTP {(int)response.StatusCode}: {err}");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispatch ntfy alert: {Error}", ex.Message);
            return Result.Failure($"ntfy error: {ex.Message}");
        }
    }

    private static async Task<Result> DispatchPagerDutyAsync(string routingKey, string summary, string source, string severity, object customDetails, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                routing_key = routingKey,
                event_action = "trigger",
                payload = new
                {
                    summary,
                    source,
                    severity,
                    component = "UniBackup",
                    group = "Database Backup",
                    custom_details = customDetails
                }
            };
            return await SendJsonWithResultAsync("https://events.pagerduty.com/v2/enqueue", payload, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispatch PagerDuty alert: {Error}", ex.Message);
            return Result.Failure($"PagerDuty error: {ex.Message}");
        }
    }

    private static async Task<Result> DispatchCustomWebhookAsync(string url, string? headerName, string? headerValue, object body, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(headerName) && !string.IsNullOrWhiteSpace(headerValue))
            {
                request.Headers.TryAddWithoutValidation(headerName.Trim(), headerValue.Trim());
            }

            var response = await HttpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"Webhook HTTP {(int)response.StatusCode}: {err}");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to dispatch custom webhook: {Error}", ex.Message);
            return Result.Failure($"Custom Webhook error: {ex.Message}");
        }
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
