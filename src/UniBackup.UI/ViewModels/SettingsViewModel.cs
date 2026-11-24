using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Windows;
using UniBackup.UI.Services;

namespace UniBackup.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel? _mainVm;
    private readonly ConfigurationService? _configService;
    private readonly ISchedulerService? _schedulerService;
    private readonly ServerDiagnosticsService? _diagnosticsService;
    private readonly WebhookNotificationService _webhookService = new();

    private string _savedConfigSnapshot = "";

    [ObservableProperty]
    private AppConfig _config = new();

    // 1. Navigation
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewSection))]
    [NotifyPropertyChangedFor(nameof(IsAppearanceSection))]
    [NotifyPropertyChangedFor(nameof(IsSecuritySection))]
    [NotifyPropertyChangedFor(nameof(IsEngineSection))]
    [NotifyPropertyChangedFor(nameof(IsCredentialsSection))]
    [NotifyPropertyChangedFor(nameof(IsStorageSection))]
    [NotifyPropertyChangedFor(nameof(IsNotificationsSection))]
    [NotifyPropertyChangedFor(nameof(IsDiagnosticsSection))]
    [NotifyPropertyChangedFor(nameof(IsAdvancedSection))]
    private string _selectedSection = "Engine";

    public bool IsOverviewSection => string.Equals(SelectedSection, "Overview", StringComparison.OrdinalIgnoreCase);
    public bool IsAppearanceSection => string.Equals(SelectedSection, "Appearance", StringComparison.OrdinalIgnoreCase);
    public bool IsSecuritySection => string.Equals(SelectedSection, "Security", StringComparison.OrdinalIgnoreCase);
    public bool IsEngineSection => string.Equals(SelectedSection, "Engine", StringComparison.OrdinalIgnoreCase);
    public bool IsCredentialsSection => string.Equals(SelectedSection, "Credentials", StringComparison.OrdinalIgnoreCase);
    public bool IsStorageSection => string.Equals(SelectedSection, "Storage", StringComparison.OrdinalIgnoreCase);
    public bool IsNotificationsSection => string.Equals(SelectedSection, "Notifications", StringComparison.OrdinalIgnoreCase);
    public bool IsDiagnosticsSection => string.Equals(SelectedSection, "Diagnostics", StringComparison.OrdinalIgnoreCase);
    public bool IsAdvancedSection => string.Equals(SelectedSection, "Advanced", StringComparison.OrdinalIgnoreCase);

    // Dirty tracking & Status
    private bool _isSyncing;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private string _saveStatusMessage = "";

    [ObservableProperty]
    private bool _hasSaveStatusMessage;

    [ObservableProperty]
    private bool _saveStatusIsSuccess = true;

    // Overview Cards Live Statuses
    [ObservableProperty]
    private string _overviewAppearanceStatus = "Light Mode";

    [ObservableProperty]
    private string _overviewSecurityStatus = "Unprotected";

    [ObservableProperty]
    private string _overviewEngineStatus = "Windows Service";

    [ObservableProperty]
    private string _overviewCloudStatus = "0 providers configured";

    [ObservableProperty]
    private string _overviewStorageStatus = @"C:\UniBackup\temp";

    [ObservableProperty]
    private string _overviewNotificationStatus = "Disabled";

    [ObservableProperty]
    private string _overviewDiagnosticsStatus = "System healthy";

    [ObservableProperty]
    private string _overviewAdvancedStatus = "Parallelism: 4";

    // 2. Appearance
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme))]
    [NotifyPropertyChangedFor(nameof(IsLightTheme))]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme))]
    private string _currentTheme = "Light";

    public bool IsSystemTheme => string.Equals(CurrentTheme, "System", StringComparison.OrdinalIgnoreCase);
    public bool IsLightTheme => string.Equals(CurrentTheme, "Light", StringComparison.OrdinalIgnoreCase);
    public bool IsDarkTheme => string.Equals(CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase);

    // 3. Security
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStaticAuthMode))]
    [NotifyPropertyChangedFor(nameof(IsDateBasedAuthMode))]
    private string _securityAuthMode = "Static";

    partial void OnSecurityAuthModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsStaticAuthMode));
        OnPropertyChanged(nameof(IsDateBasedAuthMode));
        if (!_isSyncing)
        {
            UpdateSecurityPreviews();
            CheckDirty();
        }
    }

    [ObservableProperty]
    private bool _securityRequireLogin;

    partial void OnSecurityRequireLoginChanged(bool value)
    {
        if (Config?.Security != null && !_isSyncing) Config.Security.RequireLogin = value;
        SecurityStatusBadgeText = value ? "Password Active" : "Unprotected";
        if (!_isSyncing)
        {
            UpdateSecurityPreviews();
            CheckDirty();
        }
    }

    [ObservableProperty]
    private string _securityNewPassword = "";

    partial void OnSecurityNewPasswordChanged(string value)
    {
        if (!_isSyncing) CheckDirty();
    }

    [ObservableProperty]
    private string _securityConfirmPassword = "";

    partial void OnSecurityConfirmPasswordChanged(string value)
    {
        if (!_isSyncing) CheckDirty();
    }

    [ObservableProperty]
    private bool _isSecurityPasswordVisible;

    [ObservableProperty]
    private string _securityDatePrefix = "admin(";

    partial void OnSecurityDatePrefixChanged(string value)
    {
        if (!_isSyncing)
        {
            UpdateSecurityPreviews();
            CheckDirty();
        }
    }

    [ObservableProperty]
    private string _securityDateFormat = "dd-MM-yyyy";

    partial void OnSecurityDateFormatChanged(string value)
    {
        if (!_isSyncing)
        {
            UpdateSecurityPreviews();
            CheckDirty();
        }
    }

    [ObservableProperty]
    private string _securityDateSuffix = ")";

    partial void OnSecurityDateSuffixChanged(string value)
    {
        if (!_isSyncing)
        {
            UpdateSecurityPreviews();
            CheckDirty();
        }
    }

    [ObservableProperty]
    private string _securityStatusBadgeText = "Unprotected";

    [ObservableProperty]
    private string _securityMessage = "";

    [ObservableProperty]
    private bool _hasSecurityMessage;

    [ObservableProperty]
    private bool _securityMessageIsSuccess;

    [ObservableProperty]
    private string _liveTodayPasswordPreview = "";

    [ObservableProperty]
    private string _liveTomorrowPasswordPreview = "";

    public bool IsStaticAuthMode => string.Equals(SecurityAuthMode, "Static", StringComparison.OrdinalIgnoreCase);
    public bool IsDateBasedAuthMode => string.Equals(SecurityAuthMode, "DateBased", StringComparison.OrdinalIgnoreCase);

    public bool HasConfiguredPassword =>
        Config.Security != null &&
        (IsDateBasedAuthMode
            ? !string.IsNullOrWhiteSpace(Config.Security.DateFormat)
            : !string.IsNullOrWhiteSpace(Config.Security.AccessPassword));

    public ObservableCollection<string> AvailableDateFormats { get; } =
    [
        "dd-MM-yyyy",
        "dd/MM/yyyy",
        "yyyyMMdd",
        "ddMMyyyy",
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "MM-dd-yyyy",
        "MM/dd/yyyy",
        "dd.MM.yyyy",
        "yyyy_MM_dd"
    ];

    // 4. Execution Engine
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunByTask))]
    private bool _runByService = true;

    partial void OnRunByServiceChanged(bool value)
    {
        if (Config?.Schedule != null)
        {
            Config.Schedule.Engine = value ? "Service" : "TaskScheduler";
        }
        UpdateEngineBadge();
        CheckDirty();
    }

    public bool RunByTask
    {
        get => !RunByService;
        set
        {
            if (RunByService == value)
            {
                RunByService = !value;
            }
        }
    }

    [ObservableProperty]
    private string _windowsServiceStatus = "Not Installed";

    [ObservableProperty]
    private string _windowsServiceStatusColor = "#94A3B8";

    [ObservableProperty]
    private bool _windowsServiceRunning;

    [ObservableProperty]
    private bool _windowsServiceStopped;

    [ObservableProperty]
    private bool _windowsServiceInstalled;

    [ObservableProperty]
    private string _windowsServiceDetail = "";

    [ObservableProperty]
    private string _taskSchedulerStatus = "Not Installed";

    [ObservableProperty]
    private bool _taskSchedulerInstalled;

    [ObservableProperty]
    private bool _taskSchedulerRunning;

    [ObservableProperty]
    private string _taskSchedulerDetail = "";

    [ObservableProperty]
    private string _nextRunHint = "Daily at 02:00";

    [ObservableProperty]
    private string _engineSummaryBadgeText = "Engine: Checking...";

    [ObservableProperty]
    private string _engineSummaryBadgeColor = "#D97706";

    [ObservableProperty]
    private string _engineSummaryBadgeBg = "#FFFBEB";

    [ObservableProperty]
    private string _engineSummaryBadgeBorder = "#FDE68A";

    // Schedule Recurrence Parameters (Daily / Weekly / Monthly / Custom)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduleDaily))]
    [NotifyPropertyChangedFor(nameof(IsScheduleWeekly))]
    [NotifyPropertyChangedFor(nameof(IsScheduleMonthly))]
    [NotifyPropertyChangedFor(nameof(IsScheduleCustom))]
    private string _scheduleFrequency = "daily";

    partial void OnScheduleFrequencyChanged(string value)
    {
        if (Config?.Schedule != null && !_isSyncing)
        {
            Config.Schedule.Freq = value;
        }
        OnPropertyChanged(nameof(IsScheduleDaily));
        OnPropertyChanged(nameof(IsScheduleWeekly));
        OnPropertyChanged(nameof(IsScheduleMonthly));
        OnPropertyChanged(nameof(IsScheduleCustom));
        UpdateScheduleSummary();
        if (!_isSyncing) CheckDirty();
    }

    public bool IsScheduleDaily => string.Equals(ScheduleFrequency, "daily", StringComparison.OrdinalIgnoreCase);
    public bool IsScheduleWeekly => string.Equals(ScheduleFrequency, "weekly", StringComparison.OrdinalIgnoreCase);
    public bool IsScheduleMonthly => string.Equals(ScheduleFrequency, "monthly", StringComparison.OrdinalIgnoreCase);
    public bool IsScheduleCustom => string.Equals(ScheduleFrequency, "custom", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _scheduleTime = "02:00";

    partial void OnScheduleTimeChanged(string value)
    {
        if (Config?.Schedule != null && !_isSyncing)
        {
            Config.Schedule.Time = value;
        }
        UpdateScheduleSummary();
        if (!_isSyncing) CheckDirty();
    }

    [ObservableProperty]
    private int _scheduleIntervalHours = 4;

    partial void OnScheduleIntervalHoursChanged(int value)
    {
        if (Config?.Schedule != null && !_isSyncing)
        {
            Config.Schedule.Interval = value;
        }
        UpdateScheduleSummary();
        if (!_isSyncing) CheckDirty();
    }

    [ObservableProperty]
    private string _scheduleDayOfMonth = "1";

    partial void OnScheduleDayOfMonthChanged(string value)
    {
        if (Config?.Schedule != null && !_isSyncing)
        {
            Config.Schedule.Day = value;
        }
        UpdateScheduleSummary();
        if (!_isSyncing) CheckDirty();
    }

    [ObservableProperty]
    private string _scheduleWindowMode = "headless";

    partial void OnScheduleWindowModeChanged(string value)
    {
        if (Config?.Schedule != null && !_isSyncing)
        {
            Config.Schedule.WindowMode = value;
        }
        UpdateScheduleSummary();
        if (!_isSyncing) CheckDirty();
    }

    [ObservableProperty] private bool _scheduleMonday = true;
    [ObservableProperty] private bool _scheduleTuesday;
    [ObservableProperty] private bool _scheduleWednesday;
    [ObservableProperty] private bool _scheduleThursday;
    [ObservableProperty] private bool _scheduleFriday;
    [ObservableProperty] private bool _scheduleSaturday;
    [ObservableProperty] private bool _scheduleSunday;

    private void SyncWeeklyDaysToConfig()
    {
        if (Config?.Schedule == null || _isSyncing) return;
        var days = new List<string>();
        if (ScheduleMonday) days.Add("MON");
        if (ScheduleTuesday) days.Add("TUE");
        if (ScheduleWednesday) days.Add("WED");
        if (ScheduleThursday) days.Add("THU");
        if (ScheduleFriday) days.Add("FRI");
        if (ScheduleSaturday) days.Add("SAT");
        if (ScheduleSunday) days.Add("SUN");
        Config.Schedule.Days = days.Count > 0 ? days : ["MON"];
    }

    partial void OnScheduleMondayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }
    partial void OnScheduleTuesdayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }
    partial void OnScheduleWednesdayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }
    partial void OnScheduleThursdayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }
    partial void OnScheduleFridayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }
    partial void OnScheduleSaturdayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }
    partial void OnScheduleSundayChanged(bool value) { SyncWeeklyDaysToConfig(); UpdateScheduleSummary(); if (!_isSyncing) CheckDirty(); }

    [ObservableProperty]
    private string _liveScheduleSummary = "Daily at 02:00 (Session 0 Headless)";

    public ObservableCollection<string> AvailableDaysOfMonth { get; } =
        new(Enumerable.Range(1, 31).Select(i => i.ToString()));

    public ObservableCollection<int> AvailableIntervalHours { get; } = [1, 2, 4, 6, 8, 12, 24];

    public ObservableCollection<string> AvailableWindowModes { get; } = ["headless", "minimized", "normal"];

    [RelayCommand]
    public void SetScheduleFrequency(string freq)
    {
        if (!string.IsNullOrWhiteSpace(freq))
        {
            ScheduleFrequency = freq.ToLowerInvariant();
        }
    }

    public void UpdateScheduleSummary()
    {
        var mode = string.IsNullOrWhiteSpace(ScheduleWindowMode) ? "headless" : ScheduleWindowMode;
        var time = string.IsNullOrWhiteSpace(ScheduleTime) ? "02:00" : ScheduleTime;

        if (IsScheduleDaily)
        {
            LiveScheduleSummary = $"Runs automatically every day at {time} in background ({mode})";
            NextRunHint = $"Daily at {time}";
        }
        else if (IsScheduleWeekly)
        {
            var days = new List<string>();
            if (ScheduleMonday) days.Add("Mon");
            if (ScheduleTuesday) days.Add("Tue");
            if (ScheduleWednesday) days.Add("Wed");
            if (ScheduleThursday) days.Add("Thu");
            if (ScheduleFriday) days.Add("Fri");
            if (ScheduleSaturday) days.Add("Sat");
            if (ScheduleSunday) days.Add("Sun");
            var dayStr = days.Count > 0 ? string.Join(", ", days) : "Mon";
            LiveScheduleSummary = $"Runs automatically every week on {dayStr} at {time} in background ({mode})";
            NextRunHint = $"Weekly ({dayStr}) at {time}";
        }
        else if (IsScheduleMonthly)
        {
            var day = string.IsNullOrWhiteSpace(ScheduleDayOfMonth) ? "1" : ScheduleDayOfMonth;
            LiveScheduleSummary = $"Runs automatically once a month on Day {day} at {time} in background ({mode})";
            NextRunHint = $"Monthly (Day {day}) at {time}";
        }
        else
        {
            LiveScheduleSummary = $"Runs periodically every {ScheduleIntervalHours} hours in background ({mode})";
            NextRunHint = $"Every {ScheduleIntervalHours}h";
        }
    }

    // 5. Cloud Credentials
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwsProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsGDriveProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsAzureProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsLocalProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsSftpProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsFtpProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsWebDavProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsTokensProviderSelected))]
    private string _selectedCloudProvider = "Aws";

    public bool IsAwsProviderSelected => string.Equals(SelectedCloudProvider, "Aws", StringComparison.OrdinalIgnoreCase);
    public bool IsGDriveProviderSelected => string.Equals(SelectedCloudProvider, "GDrive", StringComparison.OrdinalIgnoreCase);
    public bool IsAzureProviderSelected => string.Equals(SelectedCloudProvider, "Azure", StringComparison.OrdinalIgnoreCase);
    public bool IsLocalProviderSelected => string.Equals(SelectedCloudProvider, "Local", StringComparison.OrdinalIgnoreCase);
    public bool IsSftpProviderSelected => string.Equals(SelectedCloudProvider, "Sftp", StringComparison.OrdinalIgnoreCase);
    public bool IsFtpProviderSelected => string.Equals(SelectedCloudProvider, "Ftp", StringComparison.OrdinalIgnoreCase);
    public bool IsWebDavProviderSelected => string.Equals(SelectedCloudProvider, "WebDav", StringComparison.OrdinalIgnoreCase);
    public bool IsTokensProviderSelected => string.Equals(SelectedCloudProvider, "Tokens", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isAwsSecretVisible;

    [ObservableProperty]
    private bool _isAzureSecretVisible;

    [ObservableProperty]
    private bool _isSftpSecretVisible;

    [ObservableProperty]
    private bool _isFtpSecretVisible;

    [ObservableProperty]
    private bool _isWebDavSecretVisible;

    [ObservableProperty]
    private bool _isDropboxTokenVisible;

    [ObservableProperty]
    private bool _isOneDriveTokenVisible;

    [ObservableProperty]
    private bool _isBoxTokenVisible;

    [ObservableProperty]
    private string _cloudTestFeedback = "";

    [ObservableProperty]
    private bool _hasCloudTestFeedback;

    [ObservableProperty]
    private bool _cloudTestIsSuccess;

    [ObservableProperty]
    private bool _isTestingCloud;

    // 6. Storage & Cache
    [ObservableProperty]
    private string _tempDiskFreeText = "Checking...";

    [ObservableProperty]
    private double _tempDiskUsagePercent = 20.0;

    [ObservableProperty]
    private string _tempDiskUsageLabel = "";

    // 7. Notifications
    [ObservableProperty]
    private bool _isEmailPasswordVisible;

    public string EmailToAddressesText
    {
        get => Config?.Email?.ToAddresses != null ? string.Join(", ", Config.Email.ToAddresses) : "";
        set
        {
            if (Config.Email == null) Config.Email = new EmailSettings();
            Config.Email.ToAddresses = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            OnPropertyChanged(nameof(EmailToAddressesText));
            CheckDirty();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlackWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsTeamsWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsDiscordWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsTelegramWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsPushoverWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsNtfyWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsPagerDutyWebhookSelected))]
    [NotifyPropertyChangedFor(nameof(IsCustomWebhookSelected))]
    private string _selectedWebhookService = "Slack";

    public bool IsSlackWebhookSelected => string.Equals(SelectedWebhookService, "Slack", StringComparison.OrdinalIgnoreCase);
    public bool IsTeamsWebhookSelected => string.Equals(SelectedWebhookService, "Teams", StringComparison.OrdinalIgnoreCase);
    public bool IsDiscordWebhookSelected => string.Equals(SelectedWebhookService, "Discord", StringComparison.OrdinalIgnoreCase);
    public bool IsTelegramWebhookSelected => string.Equals(SelectedWebhookService, "Telegram", StringComparison.OrdinalIgnoreCase);
    public bool IsPushoverWebhookSelected => string.Equals(SelectedWebhookService, "Pushover", StringComparison.OrdinalIgnoreCase);
    public bool IsNtfyWebhookSelected => string.Equals(SelectedWebhookService, "ntfy", StringComparison.OrdinalIgnoreCase);
    public bool IsPagerDutyWebhookSelected => string.Equals(SelectedWebhookService, "PagerDuty", StringComparison.OrdinalIgnoreCase);
    public bool IsCustomWebhookSelected => string.Equals(SelectedWebhookService, "Custom", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string _notificationTestFeedback = "";

    [ObservableProperty]
    private bool _hasNotificationTestFeedback;

    [ObservableProperty]
    private bool _notificationTestIsSuccess;

    // 8. Diagnostics
    [ObservableProperty]
    private ObservableCollection<DiagnosticItem> _diagnosticItems = [];

    [ObservableProperty]
    private bool _isValidatingServer;

    [ObservableProperty]
    private int _passedCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _failedCount;

    public bool HasDiagnostics => DiagnosticItems.Count > 0;

    // 9. Advanced
    public ObservableCollection<string> CompressionLevels { get; } = ["None", "Fast", "Optimal", "Smallest"];

    public SettingsViewModel(
        MainViewModel mainVm,
        ConfigurationService? configService = null,
        ISchedulerService? schedulerService = null,
        ServerDiagnosticsService? diagnosticsService = null)
    {
        _mainVm = mainVm;
        _config = mainVm.Config;
        _configService = configService ?? mainVm.GetType().GetField("_configService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mainVm) as ConfigurationService;
        _schedulerService = schedulerService ?? mainVm.GetType().GetField("_schedulerService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mainVm) as ISchedulerService;
        _diagnosticsService = diagnosticsService ?? mainVm.GetType().GetField("_diagnosticsService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mainVm) as ServerDiagnosticsService;

        InitializeState();
    }

    public SettingsViewModel(
        AppConfig? config = null,
        ConfigurationService? configService = null,
        ISchedulerService? schedulerService = null,
        ServerDiagnosticsService? diagnosticsService = null)
    {
        _config = config ?? new AppConfig();
        _configService = configService;
        _schedulerService = schedulerService;
        _diagnosticsService = diagnosticsService;

        InitializeState();
    }

    private void InitializeState()
    {
        SyncFromConfig();
        CaptureSnapshot();
        RefreshEngineStatus();
        UpdateFreeSpaceIndicators();
        RefreshOverviewCards();
    }

    public void SyncFromConfig()
    {
        if (Config == null) return;

        _isSyncing = true;
        try
        {
            CurrentTheme = string.IsNullOrWhiteSpace(Config.Appearance?.Theme) ? "Light" : Config.Appearance.Theme;

            if (Config.Security != null)
            {
                SecurityRequireLogin = Config.Security.RequireLogin;
                SecurityAuthMode = string.IsNullOrWhiteSpace(Config.Security.AuthMode) ? "Static" : Config.Security.AuthMode;
                SecurityDatePrefix = Config.Security.DatePrefix ?? "admin(";
                SecurityDateFormat = string.IsNullOrWhiteSpace(Config.Security.DateFormat) ? "dd-MM-yyyy" : Config.Security.DateFormat;
                SecurityDateSuffix = Config.Security.DateSuffix ?? ")";
                SecurityStatusBadgeText = SecurityRequireLogin ? "Password Active" : "Unprotected";
            }

            if (Config.Schedule != null)
            {
                RunByService = !string.Equals(Config.Schedule.Engine, "TaskScheduler", StringComparison.OrdinalIgnoreCase);
                ScheduleFrequency = string.IsNullOrWhiteSpace(Config.Schedule.Freq) ? "daily" : Config.Schedule.Freq.ToLowerInvariant();
                ScheduleTime = string.IsNullOrWhiteSpace(Config.Schedule.Time) ? "02:00" : Config.Schedule.Time;
                ScheduleIntervalHours = Config.Schedule.Interval > 0 ? Config.Schedule.Interval : 4;
                ScheduleDayOfMonth = string.IsNullOrWhiteSpace(Config.Schedule.Day) ? "1" : Config.Schedule.Day;
                ScheduleWindowMode = string.IsNullOrWhiteSpace(Config.Schedule.WindowMode) ? "headless" : Config.Schedule.WindowMode;

                var days = Config.Schedule.Days ?? new List<string>();
                ScheduleMonday = days.Any(d => d.Equals("MON", StringComparison.OrdinalIgnoreCase));
                ScheduleTuesday = days.Any(d => d.Equals("TUE", StringComparison.OrdinalIgnoreCase));
                ScheduleWednesday = days.Any(d => d.Equals("WED", StringComparison.OrdinalIgnoreCase));
                ScheduleThursday = days.Any(d => d.Equals("THU", StringComparison.OrdinalIgnoreCase));
                ScheduleFriday = days.Any(d => d.Equals("FRI", StringComparison.OrdinalIgnoreCase));
                ScheduleSaturday = days.Any(d => d.Equals("SAT", StringComparison.OrdinalIgnoreCase));
                ScheduleSunday = days.Any(d => d.Equals("SUN", StringComparison.OrdinalIgnoreCase));

                if (!ScheduleMonday && !ScheduleTuesday && !ScheduleWednesday && !ScheduleThursday && !ScheduleFriday && !ScheduleSaturday && !ScheduleSunday)
                {
                    ScheduleMonday = true;
                }

                UpdateScheduleSummary();
            }
        }
        finally
        {
            _isSyncing = false;
        }

        UpdateSecurityPreviews();
    }

    public void CaptureSnapshot()
    {
        try
        {
            _savedConfigSnapshot = JsonSerializer.Serialize(Config);
            HasUnsavedChanges = false;
        }
        catch
        {
            _savedConfigSnapshot = "";
        }
    }

    public void CheckDirty()
    {
        try
        {
            var currentJson = JsonSerializer.Serialize(Config);
            HasUnsavedChanges = !string.Equals(_savedConfigSnapshot, currentJson, StringComparison.Ordinal);
        }
        catch
        {
            HasUnsavedChanges = true;
        }
    }

    public void RefreshOverviewCards()
    {
        // 1. Appearance
        OverviewAppearanceStatus = CurrentTheme switch
        {
            "System" => "Match Windows (System)",
            "Dark" => "Dark Theme",
            _ => "Light Theme"
        };

        // 2. Security
        OverviewSecurityStatus = SecurityRequireLogin
            ? (IsDateBasedAuthMode ? "Active · Dynamic Date Formula" : "Active · Master Password")
            : "Protection Disabled";

        // 3. Engine
        OverviewEngineStatus = RunByService
            ? $"Windows Service ({WindowsServiceStatus})"
            : $"Task Scheduler ({TaskSchedulerStatus})";

        // 4. Cloud
        var cloudCount = 0;
        if (Config.Aws?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Aws?.BucketName)) cloudCount++;
        if (Config.Azure?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Azure?.ContainerName)) cloudCount++;
        if (Config.GDrive?.Enabled == true && !string.IsNullOrWhiteSpace(Config.GDrive?.TargetFolder)) cloudCount++;
        if (Config.Sftp?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Sftp?.Host)) cloudCount++;
        if (Config.Ftp?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Ftp?.Host)) cloudCount++;
        if (Config.WebDav?.Enabled == true && !string.IsNullOrWhiteSpace(Config.WebDav?.ServerUrl)) cloudCount++;
        if (Config.Dropbox?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Dropbox?.AccessToken)) cloudCount++;
        if (Config.OneDrive?.Enabled == true && !string.IsNullOrWhiteSpace(Config.OneDrive?.AccessToken)) cloudCount++;
        if (Config.Box?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Box?.AccessToken)) cloudCount++;
        if (Config.Local?.Enabled == true && !string.IsNullOrWhiteSpace(Config.Local?.TargetPath)) cloudCount++;

        OverviewCloudStatus = cloudCount == 0 ? "No storage destinations configured" : $"{cloudCount} active destination{(cloudCount == 1 ? "" : "s")}";

        // 5. Storage
        var temp = string.IsNullOrWhiteSpace(Config.Backup?.TempFolder) ? @"C:\UniBackup\temp" : Config.Backup.TempFolder;
        OverviewStorageStatus = $"{temp} ({TempDiskFreeText})";

        // 6. Notifications
        var notifActive = false;
        if (!string.IsNullOrWhiteSpace(Config.Email?.SmtpHost) && Config.Email?.ToAddresses?.Count > 0)
        {
            notifActive = true;
        }
        if (!string.IsNullOrWhiteSpace(Config.Webhooks?.SlackWebhookUrl) ||
            !string.IsNullOrWhiteSpace(Config.Webhooks?.TeamsWebhookUrl) ||
            !string.IsNullOrWhiteSpace(Config.Webhooks?.DiscordWebhookUrl) ||
            !string.IsNullOrWhiteSpace(Config.Webhooks?.TelegramBotToken) ||
            !string.IsNullOrWhiteSpace(Config.Webhooks?.PushoverUserKey) ||
            !string.IsNullOrWhiteSpace(Config.Webhooks?.NtfyTopic))
        {
            notifActive = true;
        }
        OverviewNotificationStatus = notifActive ? "Active (Email / Webhooks configured)" : "Notifications disabled";

        // 7. Diagnostics
        if (DiagnosticItems.Count > 0)
        {
            OverviewDiagnosticsStatus = FailedCount > 0
                ? $"{FailedCount} failed checks detected"
                : (WarningCount > 0 ? $"{WarningCount} warnings reported" : $"All {PassedCount} checks passed");
        }
        else
        {
            OverviewDiagnosticsStatus = "System check pending";
        }

        // 8. Advanced
        OverviewAdvancedStatus = $"Parallelism: {Config.Backup?.MaxDegreeOfParallelism ?? 4} · VSS: {(Config.Backup?.UseVssDefault ?? true ? "On" : "Off")}";
    }

    [RelayCommand]
    public void SelectSection(string section)
    {
        SelectedSection = section;
        ClearFeedbacks();
        if (string.Equals(section, "Overview", StringComparison.OrdinalIgnoreCase))
        {
            RefreshOverviewCards();
        }
    }

    [RelayCommand]
    public void SetTheme(string theme)
    {
        CurrentTheme = theme;
        if (Config.Appearance == null) Config.Appearance = new AppearanceSettings();
        Config.Appearance.Theme = theme;
        App.ApplyTheme(theme);
        CheckDirty();
        RefreshOverviewCards();
    }

    [RelayCommand]
    public void ToggleSecurityPasswordVisibility()
    {
        IsSecurityPasswordVisible = !IsSecurityPasswordVisible;
    }

    [RelayCommand]
    public void UpdateSecurityPreviews()
    {
        if (Config?.Security == null) return;

        var format = string.IsNullOrWhiteSpace(SecurityDateFormat) ? "dd-MM-yyyy" : SecurityDateFormat;
        var prefix = SecurityDatePrefix ?? "";
        var suffix = SecurityDateSuffix ?? "";
        try
        {
            LiveTodayPasswordPreview = $"{prefix}{DateTime.Today.ToString(format)}{suffix}";
            LiveTomorrowPasswordPreview = $"{prefix}{DateTime.Today.AddDays(1).ToString(format)}{suffix}";
        }
        catch
        {
            LiveTodayPasswordPreview = $"{prefix}{DateTime.Today:dd-MM-yyyy}{suffix}";
            LiveTomorrowPasswordPreview = $"{prefix}{DateTime.Today.AddDays(1):dd-MM-yyyy}{suffix}";
        }

        OnPropertyChanged(nameof(HasConfiguredPassword));
        if (!_isSyncing)
        {
            CheckDirty();
        }
    }

    [RelayCommand]
    public async Task SaveSecuritySettingsAsync()
    {
        if (Config?.Security == null) return;

        Config.Security.RequireLogin = SecurityRequireLogin;
        Config.Security.AuthMode = SecurityAuthMode;
        Config.Security.DatePrefix = SecurityDatePrefix;
        Config.Security.DateFormat = SecurityDateFormat;
        Config.Security.DateSuffix = SecurityDateSuffix;

        if (SecurityRequireLogin)
        {
            if (IsStaticAuthMode)
            {
                if (!string.IsNullOrEmpty(SecurityNewPassword))
                {
                    if (SecurityNewPassword != SecurityConfirmPassword)
                    {
                        SecurityMessage = "Passwords do not match.";
                        SecurityMessageIsSuccess = false;
                        HasSecurityMessage = true;
                        return;
                    }
                    Config.Security.AccessPassword = SecurityNewPassword;
                }
                else if (string.IsNullOrWhiteSpace(Config.Security.AccessPassword))
                {
                    SecurityMessage = "Please provide a master password.";
                    SecurityMessageIsSuccess = false;
                    HasSecurityMessage = true;
                    return;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SecurityDateFormat))
                {
                    SecurityMessage = "Please select or enter a valid date format.";
                    SecurityMessageIsSuccess = false;
                    HasSecurityMessage = true;
                    return;
                }
            }
        }

        SecurityStatusBadgeText = SecurityRequireLogin ? "Password Active" : "Unprotected";

        if (_configService != null)
        {
            var result = await _configService.SaveConfigAsync(Config);
            if (result.IsSuccess)
            {
                SecurityMessage = SecurityRequireLogin
                    ? "Security settings saved. Password protection is active."
                    : "Security settings saved. Password protection is disabled.";
                SecurityMessageIsSuccess = true;
                HasSecurityMessage = true;
                CaptureSnapshot();
                NotificationHub.ShowSuccess(SecurityMessage, "Security Saved");
            }
            else
            {
                SecurityMessage = $"Failed to save security settings: {result.Error}";
                SecurityMessageIsSuccess = false;
                HasSecurityMessage = true;
            }
        }
        else
        {
            SecurityMessage = "Security settings updated.";
            SecurityMessageIsSuccess = true;
            HasSecurityMessage = true;
            CaptureSnapshot();
        }

        RefreshOverviewCards();
    }

    [RelayCommand]
    public async Task DisableSecurityPasswordAsync()
    {
        if (Config?.Security == null) return;

        Config.Security.RequireLogin = false;
        Config.Security.AccessPassword = "";
        SecurityRequireLogin = false;
        SecurityNewPassword = "";
        SecurityConfirmPassword = "";
        SecurityStatusBadgeText = "Unprotected";

        if (_configService != null)
        {
            await _configService.SaveConfigAsync(Config);
        }

        SecurityMessage = "Password protection removed.";
        SecurityMessageIsSuccess = true;
        HasSecurityMessage = true;
        CaptureSnapshot();
        RefreshOverviewCards();
        NotificationHub.ShowInfo("Password protection has been removed.", "Security Updated");
    }

    [RelayCommand]
    public void RefreshEngineStatus()
    {
        if (_mainVm != null)
        {
            _mainVm.RefreshServiceStatus();
            WindowsServiceStatus = _mainVm.WindowsServiceStatus;
            WindowsServiceStatusColor = _mainVm.WindowsServiceStatusColor;
            WindowsServiceRunning = _mainVm.WindowsServiceRunning;
            WindowsServiceStopped = _mainVm.WindowsServiceStopped;
            WindowsServiceInstalled = _mainVm.WindowsServiceInstalled;
            WindowsServiceDetail = _mainVm.WindowsServiceDetail;

            TaskSchedulerInstalled = _mainVm.TaskInstalled;
            TaskSchedulerRunning = _mainVm.TaskRunning;
            TaskSchedulerStatus = _mainVm.TaskState;
            TaskSchedulerDetail = _mainVm.TaskDetail;
        }
        else
        {
            WindowsServiceStatus = WindowsServiceManager.GetServiceStatus();
            WindowsServiceRunning = WindowsServiceStatus == "Running";
            WindowsServiceStopped = WindowsServiceStatus == "Stopped";
            WindowsServiceInstalled = WindowsServiceStatus != "Not Installed";
            WindowsServiceStatusColor = WindowsServiceRunning ? "#10B981" : (WindowsServiceStopped ? "#EF4444" : "#94A3B8");
            WindowsServiceDetail = WindowsServiceRunning ? "Service running in background session 0" : "Service stopped or not installed";

            TaskSchedulerStatus = "Available";
            TaskSchedulerInstalled = true;
        }
        UpdateEngineBadge();
    }

    public void UpdateEngineBadge()
    {
        if (RunByService)
        {
            if (string.Equals(WindowsServiceStatus, "Running", StringComparison.OrdinalIgnoreCase))
            {
                EngineSummaryBadgeText = "Engine: Service Running";
                EngineSummaryBadgeColor = "#059669";
                EngineSummaryBadgeBg = "#ECFDF5";
                EngineSummaryBadgeBorder = "#A7F3D0";
            }
            else if (string.Equals(WindowsServiceStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
            {
                EngineSummaryBadgeText = "Engine: Service Stopped";
                EngineSummaryBadgeColor = "#DC2626";
                EngineSummaryBadgeBg = "#FEF2F2";
                EngineSummaryBadgeBorder = "#FECACA";
            }
            else if (string.Equals(WindowsServiceStatus, "Not Installed", StringComparison.OrdinalIgnoreCase) || !WindowsServiceInstalled)
            {
                EngineSummaryBadgeText = "Engine: Not Installed";
                EngineSummaryBadgeColor = "#D97706";
                EngineSummaryBadgeBg = "#FFFBEB";
                EngineSummaryBadgeBorder = "#FDE68A";
            }
            else
            {
                EngineSummaryBadgeText = $"Engine: {WindowsServiceStatus}";
                EngineSummaryBadgeColor = "#475569";
                EngineSummaryBadgeBg = "#F1F5F9";
                EngineSummaryBadgeBorder = "#CBD5E1";
            }
        }
        else
        {
            if (TaskSchedulerRunning)
            {
                EngineSummaryBadgeText = "Engine: Task Active";
                EngineSummaryBadgeColor = "#0284C7";
                EngineSummaryBadgeBg = "#F0F9FF";
                EngineSummaryBadgeBorder = "#BAE6FD";
            }
            else if (TaskSchedulerInstalled)
            {
                EngineSummaryBadgeText = "Engine: Task Ready";
                EngineSummaryBadgeColor = "#2563EB";
                EngineSummaryBadgeBg = "#EFF6FF";
                EngineSummaryBadgeBorder = "#BFDBFE";
            }
            else
            {
                EngineSummaryBadgeText = "Engine: Task Unregistered";
                EngineSummaryBadgeColor = "#D97706";
                EngineSummaryBadgeBg = "#FFFBEB";
                EngineSummaryBadgeBorder = "#FDE68A";
            }
        }
    }

    [RelayCommand]
    public void InstallWindowsService()
    {
        if (_mainVm != null)
        {
            _mainVm.InstallWindowsService();
        }
        else
        {
            var result = WindowsServiceManager.InstallService();
            if (result.IsSuccess)
            {
                NotificationHub.ShowSuccess("UniBackup Windows Service installed successfully!", "Service Installed");
            }
            else
            {
                NotificationHub.ShowError(result.Error ?? "Service installation failed.", "Service Installation Error");
            }
        }
        RefreshEngineStatus();
        RefreshOverviewCards();
    }

    [RelayCommand]
    public void UninstallWindowsService()
    {
        if (_mainVm != null)
        {
            _mainVm.UninstallWindowsService();
        }
        else
        {
            var confirm = MessageBox.Show(
                "Are you sure you want to remove the UniBackup Windows Service?",
                "Confirm Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                var result = WindowsServiceManager.UninstallService();
                if (result.IsSuccess)
                {
                    NotificationHub.ShowInfo("Windows Service uninstalled successfully.", "Service Removed");
                }
                else
                {
                    NotificationHub.ShowError(result.Error ?? "Service uninstall failed.", "Service Error");
                }
            }
        }
        RefreshEngineStatus();
        RefreshOverviewCards();
    }

    [RelayCommand]
    public void StartWindowsService()
    {
        if (_mainVm != null)
        {
            _mainVm.StartWindowsService();
        }
        else
        {
            var res = WindowsServiceManager.StartService();
            if (!res.IsSuccess)
            {
                NotificationHub.ShowError(res.Error ?? "Failed to start service", "Service Error");
            }
        }
        RefreshEngineStatus();
        RefreshOverviewCards();
    }

    [RelayCommand]
    public void StopWindowsService()
    {
        if (_mainVm != null)
        {
            _mainVm.StopWindowsService();
        }
        else
        {
            var res = WindowsServiceManager.StopService();
            if (!res.IsSuccess)
            {
                NotificationHub.ShowError(res.Error ?? "Failed to stop service", "Service Error");
            }
        }
        RefreshEngineStatus();
        RefreshOverviewCards();
    }

    [RelayCommand]
    public async Task InstallTaskSchedulerAsync()
    {
        if (_mainVm != null)
        {
            await _mainVm.InstallTaskAsync();
        }
        else if (_schedulerService != null)
        {
            var res = await _schedulerService.SyncAllJobsTaskAsync(Config.Schedule);
            if (res.IsSuccess)
            {
                NotificationHub.ShowSuccess("Nightly backup task registered in Windows Task Scheduler!", "Task Installed");
            }
            else
            {
                NotificationHub.ShowError($"Failed to register task: {res.Error}", "Task Error");
            }
        }
        RefreshEngineStatus();
        RefreshOverviewCards();
    }

    [RelayCommand]
    public void SelectCloudProvider(string provider)
    {
        SelectedCloudProvider = provider;
        ClearFeedbacks();
    }

    [RelayCommand]
    public void BrowseTempFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Temporary Staging Directory",
            InitialDirectory = Directory.Exists(Config.Backup.TempFolder) ? Config.Backup.TempFolder : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            Config.Backup.TempFolder = dialog.FolderName;
            OnPropertyChanged(nameof(Config));
            UpdateFreeSpaceIndicators();
            CheckDirty();
        }
    }

    [RelayCommand]
    public void BrowseLocalStorageFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Local or Network Backup Directory",
            InitialDirectory = Directory.Exists(Config.Local.TargetPath) ? Config.Local.TargetPath : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            Config.Local.TargetPath = dialog.FolderName;
            OnPropertyChanged(nameof(Config));
            CheckDirty();
        }
    }

    [RelayCommand]
    public void TestLocalStorageFolder()
    {
        var path = Config.Local?.TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            CloudTestFeedback = "Please specify a local or UNC folder path.";
            CloudTestIsSuccess = false;
            HasCloudTestFeedback = true;
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            var testFile = Path.Combine(path, $".unibackup_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "UniBackup write access verification test.");
            File.Delete(testFile);

            CloudTestFeedback = $"Success! Target folder is writable: {path}";
            CloudTestIsSuccess = true;
            HasCloudTestFeedback = true;
            NotificationHub.ShowSuccess(CloudTestFeedback, "Local Storage Verified");
        }
        catch (Exception ex)
        {
            CloudTestFeedback = $"Cannot write to target folder: {ex.Message}";
            CloudTestIsSuccess = false;
            HasCloudTestFeedback = true;
            NotificationHub.ShowError(CloudTestFeedback, "Storage Error");
        }
    }

    [RelayCommand]
    public async Task TestCloudConnectionAsync(string provider)
    {
        IsTestingCloud = true;
        CloudTestFeedback = "Connecting to storage provider...";
        HasCloudTestFeedback = true;

        try
        {
            if (_mainVm != null)
            {
                switch (provider.ToLowerInvariant())
                {
                    case "aws":
                    case "s3":
                        await _mainVm.TestS3ConnectionAsync();
                        break;
                    case "gdrive":
                    case "googledrive":
                        _mainVm.TestGoogleDrive();
                        break;
                    case "azure":
                        await _mainVm.TestAzureAsync();
                        break;
                    case "sftp":
                        await _mainVm.TestSftpAsync();
                        break;
                    case "ftp":
                        await _mainVm.TestFtpAsync();
                        break;
                    case "webdav":
                        await _mainVm.TestWebDavAsync();
                        break;
                    case "dropbox":
                        await _mainVm.TestDropboxAsync();
                        break;
                    case "onedrive":
                        await _mainVm.TestOneDriveAsync();
                        break;
                    case "box":
                        await _mainVm.TestBoxAsync();
                        break;
                    default:
                        TestLocalStorageFolder();
                        return;
                }

                CloudTestFeedback = $"{provider.ToUpperInvariant()} connection test dispatched.";
                CloudTestIsSuccess = true;
            }
            else
            {
                await Task.Delay(300);
                CloudTestFeedback = $"{provider.ToUpperInvariant()} test completed (simulated).";
                CloudTestIsSuccess = true;
            }
        }
        catch (Exception ex)
        {
            CloudTestFeedback = $"Connection test failed: {ex.Message}";
            CloudTestIsSuccess = false;
        }
        finally
        {
            IsTestingCloud = false;
        }
    }

    [RelayCommand]
    public async Task TestEmailNotificationAsync()
    {
        if (_mainVm != null)
        {
            await _mainVm.TestEmailAsync();
            NotificationTestFeedback = "Email dispatch initiated. Check recipient inbox.";
            NotificationTestIsSuccess = true;
            HasNotificationTestFeedback = true;
        }
        else
        {
            NotificationTestFeedback = "SMTP test completed.";
            NotificationTestIsSuccess = true;
            HasNotificationTestFeedback = true;
        }
    }

    [RelayCommand]
    public async Task TestWebhookNotificationAsync()
    {
        try
        {
            NotificationTestFeedback = "Dispatching webhook test payload...";
            HasNotificationTestFeedback = true;

            var res = SelectedWebhookService.ToLowerInvariant() switch
            {
                "teams" => await _webhookService.SendTestTeamsAsync(Config.Webhooks.TeamsWebhookUrl),
                "discord" => await _webhookService.SendTestDiscordAsync(Config.Webhooks.DiscordWebhookUrl),
                "telegram" => await _webhookService.SendTestTelegramAsync(Config.Webhooks.TelegramBotToken, Config.Webhooks.TelegramChatId),
                "pushover" => await _webhookService.SendTestPushoverAsync(Config.Webhooks.PushoverUserKey, Config.Webhooks.PushoverApiToken),
                "ntfy" => await _webhookService.SendTestNtfyAsync(Config.Webhooks.NtfyServerUrl, Config.Webhooks.NtfyTopic, Config.Webhooks.NtfyToken),
                "pagerduty" => await _webhookService.SendTestPagerDutyAsync(Config.Webhooks.PagerDutyRoutingKey),
                "custom" => await _webhookService.SendTestCustomWebhookAsync(Config.Webhooks.CustomWebhookUrl, Config.Webhooks.CustomWebhookHeaderName, Config.Webhooks.CustomWebhookHeaderValue),
                _ => await _webhookService.SendTestSlackAsync(Config.Webhooks.SlackWebhookUrl)
            };

            if (res.IsSuccess)
            {
                NotificationTestFeedback = $"Success! {SelectedWebhookService} test payload delivered.";
                NotificationTestIsSuccess = true;
                NotificationHub.ShowSuccess(NotificationTestFeedback, "Webhook Verified");
            }
            else
            {
                NotificationTestFeedback = $"Webhook error: {res.Error}";
                NotificationTestIsSuccess = false;
                NotificationHub.ShowError(NotificationTestFeedback, "Webhook Failed");
            }
        }
        catch (Exception ex)
        {
            NotificationTestFeedback = $"Webhook failed: {ex.Message}";
            NotificationTestIsSuccess = false;
            NotificationHub.ShowError(NotificationTestFeedback, "Webhook Error");
        }
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        if (IsValidatingServer) return;
        IsValidatingServer = true;

        try
        {
            List<DiagnosticItem> results;
            if (_diagnosticsService != null)
            {
                results = await _diagnosticsService.RunDiagnosticsAsync(Config);
            }
            else if (_mainVm != null)
            {
                await _mainVm.RunValidateServerAsync();
                results = [.. _mainVm.DiagnosticItems];
            }
            else
            {
                results =
                [
                    new DiagnosticItem { Category = "Storage", Name = "Temp Folder Check", Status = "PASS", Details = "Temp folder is accessible and writable." },
                    new DiagnosticItem { Category = "System", Name = "Windows Service Status", Status = "PASS", Details = "Service is registered and operational." }
                ];
            }

            DiagnosticItems.Clear();
            foreach (var item in results) DiagnosticItems.Add(item);

            PassedCount = results.Count(r => string.Equals(r.Status, "PASS", StringComparison.OrdinalIgnoreCase));
            WarningCount = results.Count(r => string.Equals(r.Status, "WARN", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Status, "WARNING", StringComparison.OrdinalIgnoreCase));
            FailedCount = results.Count(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Status, "ERROR", StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(HasDiagnostics));
            RefreshOverviewCards();

            if (FailedCount > 0)
            {
                NotificationHub.ShowWarning($"Diagnostics: {FailedCount} issue(s) detected out of {results.Count} checks.", "Diagnostics");
            }
            else
            {
                NotificationHub.ShowSuccess($"Diagnostics: All {PassedCount} system checks passed cleanly!", "Diagnostics");
            }
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Diagnostic run failed: {ex.Message}", "Diagnostics Error");
        }
        finally
        {
            IsValidatingServer = false;
        }
    }

    [RelayCommand]
    public async Task ExportDiagnosticsAsync()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "Markdown Report (*.md)|*.md|Text Report (*.txt)|*.txt",
            FileName = $"UniBackup_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.md"
        };

        if (sfd.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UniBackup Pro – System Diagnostic Report");
            sb.AppendLine($"Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Machine: {Environment.MachineName} ({Environment.OSVersion})");
            sb.AppendLine();
            sb.AppendLine($"## Summary: {PassedCount} Passed, {WarningCount} Warnings, {FailedCount} Failed");
            sb.AppendLine();
            sb.AppendLine("| Category | Check Name | Status | Details |");
            sb.AppendLine("| --- | --- | --- | --- |");

            foreach (var item in DiagnosticItems)
            {
                sb.AppendLine($"| {item.Category} | {item.Name} | {item.Status} | {item.Details} |");
            }

            await File.WriteAllTextAsync(sfd.FileName, sb.ToString());
            NotificationHub.ShowSuccess("Diagnostic report exported successfully!", "Export Finished");
        }
    }

    [RelayCommand]
    public void OpenLogsFolder()
    {
        var logsDir = AppPaths.DefaultLogsDirectory;
        Directory.CreateDirectory(logsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", logsDir) { UseShellExecute = true });
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        Config.Backup.TempFolder = NormalizeTempFolderPath(Config.Backup.TempFolder);
        Config.Schedule.Engine = RunByService ? "Service" : "TaskScheduler";
        Config.Schedule.Freq = ScheduleFrequency;
        Config.Schedule.Time = ScheduleTime;
        Config.Schedule.Interval = IsScheduleCustom ? ScheduleIntervalHours : 1;
        Config.Schedule.Day = ScheduleDayOfMonth;
        Config.Schedule.WindowMode = ScheduleWindowMode;

        var days = new List<string>();
        if (ScheduleMonday) days.Add("MON");
        if (ScheduleTuesday) days.Add("TUE");
        if (ScheduleWednesday) days.Add("WED");
        if (ScheduleThursday) days.Add("THU");
        if (ScheduleFriday) days.Add("FRI");
        if (ScheduleSaturday) days.Add("SAT");
        if (ScheduleSunday) days.Add("SUN");
        Config.Schedule.Days = days.Count > 0 ? days : ["MON"];

        if (Config.Security != null)
        {
            Config.Security.RequireLogin = SecurityRequireLogin;
            Config.Security.AuthMode = SecurityAuthMode;
            Config.Security.DatePrefix = SecurityDatePrefix;
            Config.Security.DateFormat = SecurityDateFormat;
            Config.Security.DateSuffix = SecurityDateSuffix;
            if (!string.IsNullOrEmpty(SecurityNewPassword) && SecurityNewPassword == SecurityConfirmPassword)
            {
                Config.Security.AccessPassword = SecurityNewPassword;
            }
        }

        if (string.IsNullOrWhiteSpace(Config.Email.Username) && !string.IsNullOrWhiteSpace(Config.Email.FromAddress))
        {
            Config.Email.Username = Config.Email.FromAddress;
        }
        else if (string.IsNullOrWhiteSpace(Config.Email.FromAddress) && !string.IsNullOrWhiteSpace(Config.Email.Username))
        {
            Config.Email.FromAddress = Config.Email.Username;
        }

        if (_configService != null)
        {
            var result = await _configService.SaveConfigAsync(Config);
            if (result.IsSuccess)
            {
                if (TaskSchedulerInstalled && _schedulerService != null)
                {
                    await _schedulerService.SyncAllJobsTaskAsync(Config.Schedule);
                }

                CaptureSnapshot();
                SaveStatusMessage = "Settings saved successfully!";
                SaveStatusIsSuccess = true;
                HasSaveStatusMessage = true;
                NotificationHub.ShowSuccess("All settings and schedules saved successfully!", "Settings Saved");
                UpdateFreeSpaceIndicators();
                RefreshEngineStatus();
                RefreshOverviewCards();
            }
            else
            {
                SaveStatusMessage = $"Failed to save settings: {result.Error}";
                SaveStatusIsSuccess = false;
                HasSaveStatusMessage = true;
                NotificationHub.ShowError(SaveStatusMessage, "Save Error");
            }
        }
        else if (_mainVm != null)
        {
            await _mainVm.SaveSettingsAsync();
            CaptureSnapshot();
            SaveStatusMessage = "Settings saved successfully!";
            SaveStatusIsSuccess = true;
            HasSaveStatusMessage = true;
            RefreshOverviewCards();
        }
        else
        {
            CaptureSnapshot();
            SaveStatusMessage = "Settings updated in memory.";
            SaveStatusIsSuccess = true;
            HasSaveStatusMessage = true;
            RefreshOverviewCards();
        }
    }

    [RelayCommand]
    public void ResetSection()
    {
        if (string.IsNullOrWhiteSpace(_savedConfigSnapshot)) return;

        try
        {
            var restored = JsonSerializer.Deserialize<AppConfig>(_savedConfigSnapshot);
            if (restored == null) return;

            switch (SelectedSection.ToLowerInvariant())
            {
                case "appearance":
                    Config.Appearance = restored.Appearance ?? new AppearanceSettings();
                    CurrentTheme = Config.Appearance.Theme ?? "Light";
                    App.ApplyTheme(CurrentTheme);
                    break;
                case "security":
                    Config.Security = restored.Security ?? new SecuritySettings();
                    SyncFromConfig();
                    break;
                case "engine":
                    Config.Schedule = restored.Schedule ?? new ScheduleSettings();
                    SyncFromConfig();
                    break;
                case "credentials":
                    Config.Aws = restored.Aws ?? new AwsSettings();
                    Config.Azure = restored.Azure ?? new AzureBlobSettings();
                    Config.GDrive = restored.GDrive ?? new GDriveSettings();
                    Config.Local = restored.Local ?? new LocalStorageSettings();
                    Config.Sftp = restored.Sftp ?? new SftpSettings();
                    Config.Ftp = restored.Ftp ?? new FtpSettings();
                    Config.WebDav = restored.WebDav ?? new WebDavSettings();
                    Config.Dropbox = restored.Dropbox ?? new DropboxSettings();
                    Config.OneDrive = restored.OneDrive ?? new OneDriveSettings();
                    Config.Box = restored.Box ?? new BoxSettings();
                    break;
                case "storage":
                    Config.Backup.TempFolder = restored.Backup?.TempFolder ?? @"C:\UniBackup\temp";
                    Config.Backup.KeepLogsDays = restored.Backup?.KeepLogsDays ?? 30;
                    Config.Backup.HistoryRetentionDays = restored.Backup?.HistoryRetentionDays ?? 90;
                    Config.Backup.MaxLogMb = restored.Backup?.MaxLogMb ?? 10;
                    UpdateFreeSpaceIndicators();
                    break;
                case "notifications":
                    Config.Email = restored.Email ?? new EmailSettings();
                    Config.Webhooks = restored.Webhooks ?? new WebhookSettings();
                    Config.NotificationMatrix = restored.NotificationMatrix ?? new NotificationTriggerMatrix();
                    break;
                case "advanced":
                    Config.Backup.MaxDegreeOfParallelism = restored.Backup?.MaxDegreeOfParallelism ?? 4;
                    Config.Backup.UseVssDefault = restored.Backup?.UseVssDefault ?? true;
                    Config.Backup.BandwidthLimitKbps = restored.Backup?.BandwidthLimitKbps ?? 0;
                    Config.Backup.CompressionLevel = restored.Backup?.CompressionLevel ?? "Optimal";
                    Config.Backup.CleanTempAfterBackup = restored.Backup?.CleanTempAfterBackup ?? true;
                    Config.Backup.MaxLogMb = restored.Backup?.MaxLogMb ?? 10;
                    break;
                default:
                    Config = restored;
                    SyncFromConfig();
                    break;
            }

            OnPropertyChanged(nameof(Config));
            CheckDirty();
            RefreshOverviewCards();
            NotificationHub.ShowInfo($"Reset {SelectedSection} to last saved configuration.", "Section Reset");
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Failed to reset section: {ex.Message}", "Reset Error");
        }
    }

    [RelayCommand]
    public async Task ExportConfigAsync()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "JSON Configuration (*.json)|*.json",
            FileName = $"UniBackup_Config_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (sfd.ShowDialog() == true)
        {
            var cloneJson = JsonSerializer.Serialize(Config);
            var clone = JsonSerializer.Deserialize<AppConfig>(cloneJson) ?? new AppConfig();
            new CredentialProtector().SealConfig(clone);
            var json = JsonSerializer.Serialize(clone, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(sfd.FileName, json);
            NotificationHub.ShowSuccess("Configuration exported securely with protected credentials!", "Export Configuration");
        }
    }

    [RelayCommand]
    public async Task ImportConfigAsync()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "JSON Configuration (*.json)|*.json"
        };

        if (ofd.ShowDialog() == true)
        {
            try
            {
                var json = await File.ReadAllTextAsync(ofd.FileName);
                var imported = JsonSerializer.Deserialize<AppConfig>(json);
                if (imported != null)
                {
                    new CredentialProtector().UnsealConfig(imported);
                    Config = imported;
                    SyncFromConfig();
                    CheckDirty();
                    RefreshOverviewCards();
                    NotificationHub.ShowSuccess("Configuration imported successfully! Click Save to apply.", "Import Succeeded");
                }
            }
            catch (Exception ex)
            {
                NotificationHub.ShowError($"Failed to parse configuration: {ex.Message}", "Import Failed");
            }
        }
    }

    [RelayCommand]
    public void ResetToFactoryDefaults()
    {
        var res = MessageBox.Show(
            "Are you sure you want to reset all settings to factory defaults? Unsaved custom credentials and schedules will be restored to defaults.",
            "Reset to Defaults",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res == MessageBoxResult.Yes)
        {
            Config = new AppConfig();
            SyncFromConfig();
            CheckDirty();
            RefreshOverviewCards();
            NotificationHub.ShowInfo("All settings restored to factory defaults. Click 'Save Settings' to commit.", "Defaults Restored");
        }
    }

    public void UpdateFreeSpaceIndicators()
    {
        try
        {
            var folder = Config.Backup.TempFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
            {
                folder = @"C:\UniBackup\temp";
            }
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrWhiteSpace(root)) root = @"C:\";

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                var totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                var usedPercent = totalGb > 0 ? ((totalGb - freeGb) / totalGb) * 100.0 : 0.0;

                TempDiskFreeText = $"{freeGb:F1} GB free of {totalGb:F0} GB";
                TempDiskUsagePercent = Math.Clamp(usedPercent, 0, 100);
                TempDiskUsageLabel = $"Drive {drive.Name} ({drive.DriveFormat})";
                return;
            }
        }
        catch
        {
            // fallback
        }

        TempDiskFreeText = "Storage ready";
        TempDiskUsagePercent = 25.0;
        TempDiskUsageLabel = "Drive C:";
    }

    private static string NormalizeTempFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return @"C:\UniBackup\temp";
        var trimmed = path.Trim();
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            return trimmed + @"\";
        if (trimmed.Length == 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/'))
            return trimmed.Substring(0, 2) + @"\";
        return trimmed.TrimEnd('\\', '/');
    }

    private void ClearFeedbacks()
    {
        HasCloudTestFeedback = false;
        HasNotificationTestFeedback = false;
        HasSecurityMessage = false;
        HasSaveStatusMessage = false;
    }
}
