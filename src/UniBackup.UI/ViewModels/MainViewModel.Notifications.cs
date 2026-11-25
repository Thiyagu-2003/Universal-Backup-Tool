using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.UI.Services;

namespace UniBackup.UI.ViewModels;

public sealed class NotificationActivityItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string Provider { get; set; } = "Email";
    public string RelatedJob { get; set; } = "-";
    public string EventType { get; set; } = "Test Dispatch";
    public string Result { get; set; } = "Success"; // Success | Failed
    public bool IsSuccess => string.Equals(Result, "Success", StringComparison.OrdinalIgnoreCase);
    public string ErrorDetails { get; set; } = "";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorDetails);
}

public partial class MainViewModel
{
    // =========================================================================
    // 1. HEADER SUMMARY CARDS
    // =========================================================================

    [ObservableProperty]
    private int _configuredChannelsCount;

    [ObservableProperty]
    private int _activeChannelsCount;

    [ObservableProperty]
    private int _failedTestNotificationsCount;

    [ObservableProperty]
    private string _lastNotificationActivityDisplay = "No activity yet";

    // =========================================================================
    // 2. FLOATING PROVIDER SUBMENU STATE (All 10 Providers)
    // =========================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviderEmail))]
    [NotifyPropertyChangedFor(nameof(IsProviderSlack))]
    [NotifyPropertyChangedFor(nameof(IsProviderTeams))]
    [NotifyPropertyChangedFor(nameof(IsProviderDiscord))]
    [NotifyPropertyChangedFor(nameof(IsProviderTelegram))]
    [NotifyPropertyChangedFor(nameof(IsProviderPushover))]
    [NotifyPropertyChangedFor(nameof(IsProviderNtfy))]
    [NotifyPropertyChangedFor(nameof(IsProviderPagerDuty))]
    [NotifyPropertyChangedFor(nameof(IsProviderCustomWebhook))]
    [NotifyPropertyChangedFor(nameof(IsProviderHeartbeat))]
    private string _selectedNotificationProvider = "Email";

    public bool IsProviderEmail => SelectedNotificationProvider == "Email";
    public bool IsProviderSlack => SelectedNotificationProvider == "Slack";
    public bool IsProviderTeams => SelectedNotificationProvider == "Teams";
    public bool IsProviderDiscord => SelectedNotificationProvider == "Discord";
    public bool IsProviderTelegram => SelectedNotificationProvider == "Telegram";
    public bool IsProviderPushover => SelectedNotificationProvider == "Pushover";
    public bool IsProviderNtfy => SelectedNotificationProvider == "Ntfy";
    public bool IsProviderPagerDuty => SelectedNotificationProvider == "PagerDuty";
    public bool IsProviderCustomWebhook => SelectedNotificationProvider == "CustomWebhook";
    public bool IsProviderHeartbeat => SelectedNotificationProvider == "Heartbeat";

    public bool IsEmailConfigured => Config?.Email != null && Config.Email.Enabled && !string.IsNullOrWhiteSpace(Config.Email.SmtpHost);
    public bool IsSlackConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.SlackWebhookUrl);
    public bool IsTeamsConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.TeamsWebhookUrl);
    public bool IsDiscordConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.DiscordWebhookUrl);
    public bool IsTelegramConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.TelegramBotToken) && !string.IsNullOrWhiteSpace(Config?.Webhooks?.TelegramChatId);
    public bool IsPushoverConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.PushoverUserKey) && !string.IsNullOrWhiteSpace(Config?.Webhooks?.PushoverApiToken);
    public bool IsNtfyConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.NtfyTopic);
    public bool IsPagerDutyConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.PagerDutyRoutingKey);
    public bool IsCustomWebhookConfigured => !string.IsNullOrWhiteSpace(Config?.Webhooks?.CustomWebhookUrl);
    public bool IsHeartbeatConfigured => Config?.Heartbeat != null && Config.Heartbeat.Enabled && !string.IsNullOrWhiteSpace(Config.Heartbeat.Url);

    [RelayCommand]
    public void SelectNotificationProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return;
        SelectedNotificationProvider = provider;
    }

    // =========================================================================
    // 3. NOTIFICATION RULES (WHEN TO NOTIFY) COLUMN TOGGLES
    // =========================================================================

    [RelayCommand]
    public void ToggleAllSuccessNotifications()
    {
        var m = Config.NotificationMatrix;
        bool target = !(m.FullSuccess && m.DiffSuccess && m.LogSuccess);
        m.FullSuccess = target;
        m.DiffSuccess = target;
        m.LogSuccess = target;
        NotifyMatrixChanged();
    }

    [RelayCommand]
    public void ToggleAllWarningNotifications()
    {
        var m = Config.NotificationMatrix;
        bool target = !(m.FullWarning && m.DiffWarning && m.LogWarning);
        m.FullWarning = target;
        m.DiffWarning = target;
        m.LogWarning = target;
        NotifyMatrixChanged();
    }

    [RelayCommand]
    public void ToggleAllFailureNotifications()
    {
        var m = Config.NotificationMatrix;
        bool target = !(m.FullFailure && m.DiffFailure && m.LogFailure);
        m.FullFailure = target;
        m.DiffFailure = target;
        m.LogFailure = target;
        NotifyMatrixChanged();
    }

    private void NotifyMatrixChanged()
    {
        OnPropertyChanged(nameof(Config));
        MarkNotificationsDirty();
    }

    // =========================================================================
    // 4. UNSAVED CHANGES, VALIDATION & BOTTOM ACTION BAR
    // =========================================================================

    [ObservableProperty]
    private bool _hasUnsavedNotificationChanges;

    private string? _notificationsSnapshotJson;

    public void MarkNotificationsDirty()
    {
        HasUnsavedNotificationChanges = true;
        RefreshNotificationSummary();
    }

    private void CaptureNotificationsSnapshot()
    {
        try
        {
            _notificationsSnapshotJson = JsonSerializer.Serialize(new
            {
                Config.Email,
                Config.Webhooks,
                Config.NotificationMatrix,
                Config.Heartbeat
            });
            HasUnsavedNotificationChanges = false;
        }
        catch
        {
            HasUnsavedNotificationChanges = false;
        }
    }

    [RelayCommand]
    public async Task SaveNotificationsPageAsync()
    {
        await SaveSettingsAsync();
        CaptureNotificationsSnapshot();
        RefreshNotificationSummary();
    }

    [RelayCommand]
    public async Task TestSelectedNotificationProviderAsync()
    {
        switch (SelectedNotificationProvider)
        {
            case "Email": await TestEmailAsync(); break;
            case "Slack": await TestSlackAsync(); break;
            case "Teams": await TestTeamsAsync(); break;
            case "Discord": await TestDiscordAsync(); break;
            case "Telegram": await TestTelegramAsync(); break;
            case "Pushover": await TestPushoverAsync(); break;
            case "Ntfy": await TestNtfyAsync(); break;
            case "PagerDuty": await TestPagerDutyAsync(); break;
            case "CustomWebhook": await TestCustomWebhookAsync(); break;
            case "Heartbeat": await TestHeartbeatAsync(); break;
        }
    }

    [RelayCommand]
    public void ValidateNotificationsConfig()
    {
        var issues = new List<string>();

        if (Config.Email.Enabled)
        {
            if (string.IsNullOrWhiteSpace(Config.Email.SmtpHost)) issues.Add("Email: SMTP host is missing.");
            if (Config.Email.SmtpPort <= 0 || Config.Email.SmtpPort > 65535) issues.Add("Email: SMTP port must be between 1 and 65535.");
            if (Config.Email.ToAddresses.Count == 0) issues.Add("Email: At least one recipient is required.");
        }

        if (!string.IsNullOrWhiteSpace(Config.Webhooks.SlackWebhookUrl) && !Uri.IsWellFormedUriString(Config.Webhooks.SlackWebhookUrl, UriKind.Absolute))
            issues.Add("Slack: Webhook URL is not a valid absolute URL.");
        if (!string.IsNullOrWhiteSpace(Config.Webhooks.TeamsWebhookUrl) && !Uri.IsWellFormedUriString(Config.Webhooks.TeamsWebhookUrl, UriKind.Absolute))
            issues.Add("Teams: Webhook URL is not a valid absolute URL.");
        if (!string.IsNullOrWhiteSpace(Config.Webhooks.DiscordWebhookUrl) && !Uri.IsWellFormedUriString(Config.Webhooks.DiscordWebhookUrl, UriKind.Absolute))
            issues.Add("Discord: Webhook URL is not a valid absolute URL.");
        if (!string.IsNullOrWhiteSpace(Config.Webhooks.CustomWebhookUrl) && !Uri.IsWellFormedUriString(Config.Webhooks.CustomWebhookUrl, UriKind.Absolute))
            issues.Add("Custom Webhook: URL is not a valid absolute URL.");
        if (Config.Heartbeat.Enabled && !Uri.IsWellFormedUriString(Config.Heartbeat.Url, UriKind.Absolute))
            issues.Add("Heartbeat: Healthcheck ping URL is not a valid absolute URL.");

        if (issues.Count == 0)
        {
            NotificationHub.ShowSuccess("All configured notification channels and URLs are valid.", "Validation Passed");
        }
        else
        {
            NotificationHub.ShowWarning(string.Join("\n", issues.Take(4)), $"Validation Found {issues.Count} Issue(s)");
        }
    }

    [RelayCommand]
    public void ResetNotifications()
    {
        if (HasUnsavedNotificationChanges)
        {
            var confirm = MessageBox.Show(
                "Discard all unsaved notification configuration changes and revert to the last saved state?",
                "Reset Notification Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        if (!string.IsNullOrEmpty(_notificationsSnapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(_notificationsSnapshotJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Email", out var em))
                    Config.Email = JsonSerializer.Deserialize<EmailSettings>(em.GetRawText()) ?? new EmailSettings();
                if (root.TryGetProperty("Webhooks", out var wh))
                    Config.Webhooks = JsonSerializer.Deserialize<WebhookSettings>(wh.GetRawText()) ?? new WebhookSettings();
                if (root.TryGetProperty("NotificationMatrix", out var nm))
                    Config.NotificationMatrix = JsonSerializer.Deserialize<NotificationTriggerMatrix>(nm.GetRawText()) ?? new NotificationTriggerMatrix();
                if (root.TryGetProperty("Heartbeat", out var hb))
                    Config.Heartbeat = JsonSerializer.Deserialize<HeartbeatSettings>(hb.GetRawText()) ?? new HeartbeatSettings();

                EmailRecipientsText = string.Join(", ", Config.Email.ToAddresses);
                OnPropertyChanged(nameof(Config));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to restore notification snapshot");
            }
        }

        HasUnsavedNotificationChanges = false;
        RefreshNotificationSummary();
        NotificationHub.ShowInfo("Notification settings reverted to last saved state.", "Changes Reset");
    }

    // =========================================================================
    // 5. NOTIFICATION ACTIVITY HISTORY & FILTERS
    // =========================================================================

    private readonly List<NotificationActivityItem> _allNotificationActivity = [];

    public ObservableCollection<NotificationActivityItem> NotificationActivityPaged { get; } = [];

    public ObservableCollection<string> NotificationProviderFilterOptions { get; } =
    [
        "All Providers", "Email", "Slack", "Teams", "Discord", "Telegram", "Pushover", "ntfy", "PagerDuty", "Custom Webhook", "Heartbeat"
    ];

    public ObservableCollection<string> NotificationStatusFilterOptions { get; } =
    [
        "All Statuses", "Success", "Failed"
    ];

    public ObservableCollection<string> NotificationDateFilterOptions { get; } =
    [
        "All Time", "Today", "Last 7 Days", "Last 30 Days"
    ];

    [ObservableProperty]
    private string _notificationSearchQuery = "";

    partial void OnNotificationSearchQueryChanged(string value)
    {
        NotificationActivityCurrentPage = 1;
        ApplyNotificationActivityFilters();
    }

    [ObservableProperty]
    private string _notificationProviderFilter = "All Providers";

    partial void OnNotificationProviderFilterChanged(string value)
    {
        NotificationActivityCurrentPage = 1;
        ApplyNotificationActivityFilters();
    }

    [ObservableProperty]
    private string _notificationStatusFilter = "All Statuses";

    partial void OnNotificationStatusFilterChanged(string value)
    {
        NotificationActivityCurrentPage = 1;
        ApplyNotificationActivityFilters();
    }

    [ObservableProperty]
    private string _notificationDateFilter = "All Time";

    partial void OnNotificationDateFilterChanged(string value)
    {
        NotificationActivityCurrentPage = 1;
        ApplyNotificationActivityFilters();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNotificationPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanNotificationNextPage))]
    private int _notificationActivityCurrentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNotificationPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanNotificationNextPage))]
    private int _notificationActivityTotalPages = 1;

    [ObservableProperty]
    private int _notificationActivityPageSize = 8;

    [ObservableProperty]
    private string _notificationActivitySummary = "0 notification events";

    [ObservableProperty]
    private bool _hasNotificationActivity;

    public bool CanNotificationPrevPage => NotificationActivityCurrentPage > 1;
    public bool CanNotificationNextPage => NotificationActivityCurrentPage < NotificationActivityTotalPages;

    private List<NotificationActivityItem> _filteredNotificationActivity = [];

    [RelayCommand]
    public void NotificationPrevPage()
    {
        if (CanNotificationPrevPage)
        {
            NotificationActivityCurrentPage--;
            UpdateNotificationActivityPage();
        }
    }

    [RelayCommand]
    public void NotificationNextPage()
    {
        if (CanNotificationNextPage)
        {
            NotificationActivityCurrentPage++;
            UpdateNotificationActivityPage();
        }
    }

    [ObservableProperty]
    private NotificationActivityItem? _selectedNotificationActivity;

    [ObservableProperty]
    private bool _isNotificationActivityDetailsOpen;

    [RelayCommand]
    public void ViewNotificationActivityDetails(NotificationActivityItem? item)
    {
        if (item == null) return;
        SelectedNotificationActivity = item;
        IsNotificationActivityDetailsOpen = true;
    }

    [RelayCommand]
    public void CloseNotificationActivityDetails()
    {
        IsNotificationActivityDetailsOpen = false;
        SelectedNotificationActivity = null;
    }

    public void RecordNotificationDispatch(string provider, string eventType, bool success, string? error = null, string relatedJob = "-")
    {
        var item = new NotificationActivityItem
        {
            Timestamp = DateTime.Now,
            Provider = provider,
            RelatedJob = relatedJob,
            EventType = eventType,
            Result = success ? "Success" : "Failed",
            ErrorDetails = error ?? ""
        };

        _allNotificationActivity.Insert(0, item);
        RefreshNotificationSummary();
        ApplyNotificationActivityFilters();
    }

    [RelayCommand]
    public async Task RefreshNotificationActivityAsync()
    {
        var logItems = await Task.Run(() =>
        {
            var results = new List<NotificationActivityItem>();
            var paths = new[]
            {
                UniBackup.Infrastructure.Logging.LoggingSetup.ManualLogPath,
                UniBackup.Infrastructure.Logging.LoggingSetup.ServiceLogPath
            };

            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                try
                {
                    using var stream = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var lines = new List<string>();
                    while (reader.ReadLine() is { } line) lines.Add(line);

                    foreach (var line in lines.TakeLast(1500))
                    {
                        var parsed = new LogLine(line, 0);
                        if (!string.Equals(parsed.Source, "Notification Service", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string provider = InferNotificationProvider(parsed.Message);
                        results.Add(new NotificationActivityItem
                        {
                            Timestamp = parsed.Timestamp == DateTime.MinValue ? DateTime.Now : parsed.Timestamp,
                            Provider = provider,
                            RelatedJob = string.IsNullOrWhiteSpace(parsed.JobName) ? "-" : parsed.JobName,
                            EventType = parsed.Message.Contains("test", StringComparison.OrdinalIgnoreCase) ? "Test Dispatch" : "Backup Alert",
                            Result = parsed.IsError ? "Failed" : "Success",
                            ErrorDetails = parsed.IsError ? parsed.Message : ""
                        });
                    }
                }
                catch
                {
                    // Skip locked or unreadable log file
                }
            }

            return results.OrderByDescending(r => r.Timestamp).Take(100).ToList();
        });

        // Merge with in-session dispatches without duplicating
        var existingTimes = _allNotificationActivity.Select(x => (x.Provider, x.Timestamp.ToString("yyyyMMddHHmmss"))).ToHashSet();
        foreach (var item in logItems)
        {
            var key = (item.Provider, item.Timestamp.ToString("yyyyMMddHHmmss"));
            if (!existingTimes.Contains(key))
            {
                _allNotificationActivity.Add(item);
            }
        }

        var ordered = _allNotificationActivity.OrderByDescending(x => x.Timestamp).ToList();
        _allNotificationActivity.Clear();
        _allNotificationActivity.AddRange(ordered);

        RefreshNotificationSummary();
        ApplyNotificationActivityFilters();
    }

    private static string InferNotificationProvider(string msg)
    {
        if (msg.Contains("telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (msg.Contains("pushover", StringComparison.OrdinalIgnoreCase)) return "Pushover";
        if (msg.Contains("ntfy", StringComparison.OrdinalIgnoreCase)) return "ntfy";
        if (msg.Contains("pagerduty", StringComparison.OrdinalIgnoreCase)) return "PagerDuty";
        if (msg.Contains("slack", StringComparison.OrdinalIgnoreCase)) return "Slack";
        if (msg.Contains("teams", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (msg.Contains("discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (msg.Contains("heartbeat", StringComparison.OrdinalIgnoreCase)) return "Heartbeat";
        if (msg.Contains("webhook", StringComparison.OrdinalIgnoreCase)) return "Custom Webhook";
        return "Email";
    }

    public void ApplyNotificationActivityFilters()
    {
        IEnumerable<NotificationActivityItem> query = _allNotificationActivity;

        if (!string.IsNullOrWhiteSpace(NotificationSearchQuery))
        {
            var term = NotificationSearchQuery.Trim();
            query = query.Where(x =>
                x.Provider.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.RelatedJob.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.EventType.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.ErrorDetails.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(NotificationProviderFilter) && NotificationProviderFilter != "All Providers")
        {
            query = query.Where(x => string.Equals(x.Provider, NotificationProviderFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(NotificationStatusFilter) && NotificationStatusFilter != "All Statuses")
        {
            query = query.Where(x => string.Equals(x.Result, NotificationStatusFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (NotificationDateFilter == "Today")
        {
            var today = DateTime.Today;
            query = query.Where(x => x.Timestamp.Date == today);
        }
        else if (NotificationDateFilter == "Last 7 Days")
        {
            var cutoff = DateTime.Now.AddDays(-7);
            query = query.Where(x => x.Timestamp >= cutoff);
        }
        else if (NotificationDateFilter == "Last 30 Days")
        {
            var cutoff = DateTime.Now.AddDays(-30);
            query = query.Where(x => x.Timestamp >= cutoff);
        }

        _filteredNotificationActivity = query.ToList();
        HasNotificationActivity = _filteredNotificationActivity.Count > 0;
        UpdateNotificationActivityPage();
    }

    private void UpdateNotificationActivityPage()
    {
        int size = Math.Max(1, NotificationActivityPageSize);
        NotificationActivityTotalPages = Math.Max(1, (int)Math.Ceiling(_filteredNotificationActivity.Count / (double)size));
        if (NotificationActivityCurrentPage > NotificationActivityTotalPages) NotificationActivityCurrentPage = NotificationActivityTotalPages;
        if (NotificationActivityCurrentPage < 1) NotificationActivityCurrentPage = 1;

        var items = _filteredNotificationActivity
            .Skip((NotificationActivityCurrentPage - 1) * size)
            .Take(size)
            .ToList();

        NotificationActivityPaged.Clear();
        foreach (var item in items)
        {
            NotificationActivityPaged.Add(item);
        }

        NotificationActivitySummary = _filteredNotificationActivity.Count == 0
            ? "0 notification events"
            : $"Showing {NotificationActivityPaged.Count} of {_filteredNotificationActivity.Count} events";

        OnPropertyChanged(nameof(CanNotificationPrevPage));
        OnPropertyChanged(nameof(CanNotificationNextPage));
    }

    // =========================================================================
    // 6. INITIALIZATION & SUMMARY COMPUTATION
    // =========================================================================

    public void InitNotifications()
    {
        CaptureNotificationsSnapshot();
        RefreshNotificationSummary();
    }

    public void RefreshNotificationSummary()
    {
        var configuredStates = new[]
        {
            IsEmailConfigured,
            IsSlackConfigured,
            IsTeamsConfigured,
            IsDiscordConfigured,
            IsTelegramConfigured,
            IsPushoverConfigured,
            IsNtfyConfigured,
            IsPagerDutyConfigured,
            IsCustomWebhookConfigured,
            IsHeartbeatConfigured
        };

        ConfiguredChannelsCount = configuredStates.Count(c => c);
        ActiveChannelsCount = configuredStates.Count(c => c);
        FailedTestNotificationsCount = _allNotificationActivity.Count(a => !a.IsSuccess);

        var latest = _allNotificationActivity.OrderByDescending(a => a.Timestamp).FirstOrDefault();
        LastNotificationActivityDisplay = latest != null
            ? $"{latest.Provider} · {latest.Timestamp:MMM d, h:mm tt}"
            : "No activity yet";

        OnPropertyChanged(nameof(IsEmailConfigured));
        OnPropertyChanged(nameof(IsSlackConfigured));
        OnPropertyChanged(nameof(IsTeamsConfigured));
        OnPropertyChanged(nameof(IsDiscordConfigured));
        OnPropertyChanged(nameof(IsTelegramConfigured));
        OnPropertyChanged(nameof(IsPushoverConfigured));
        OnPropertyChanged(nameof(IsNtfyConfigured));
        OnPropertyChanged(nameof(IsPagerDutyConfigured));
        OnPropertyChanged(nameof(IsCustomWebhookConfigured));
        OnPropertyChanged(nameof(IsHeartbeatConfigured));
    }
}
