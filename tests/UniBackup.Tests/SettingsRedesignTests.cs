using System.IO;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class SettingsRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CredentialProtector _crypto;
    private readonly ConfigurationService _configService;

    public SettingsRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _crypto = new CredentialProtector();
        _configService = new ConfigurationService(_crypto, _tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void SettingsViewModel_SubmenuNavigation_SwitchesSectionsAndFlagsCorrectly()
    {
        var vm = new SettingsViewModel(new AppConfig(), _configService);

        Assert.Equal("Engine", vm.SelectedSection);
        Assert.True(vm.IsEngineSection);
        Assert.False(vm.IsAppearanceSection);

        vm.SelectSection("Appearance");
        Assert.Equal("Appearance", vm.SelectedSection);
        Assert.True(vm.IsAppearanceSection);
        Assert.False(vm.IsEngineSection);

        vm.SelectSection("Security");
        Assert.Equal("Security", vm.SelectedSection);
        Assert.True(vm.IsSecuritySection);

        vm.SelectSection("Engine");
        Assert.Equal("Engine", vm.SelectedSection);
        Assert.True(vm.IsEngineSection);

        vm.SelectSection("Credentials");
        Assert.Equal("Credentials", vm.SelectedSection);
        Assert.True(vm.IsCredentialsSection);

        vm.SelectSection("Storage");
        Assert.Equal("Storage", vm.SelectedSection);
        Assert.True(vm.IsStorageSection);

        vm.SelectSection("Notifications");
        Assert.Equal("Notifications", vm.SelectedSection);
        Assert.True(vm.IsNotificationsSection);

        vm.SelectSection("Diagnostics");
        Assert.Equal("Diagnostics", vm.SelectedSection);
        Assert.True(vm.IsDiagnosticsSection);

        vm.SelectSection("Advanced");
        Assert.Equal("Advanced", vm.SelectedSection);
        Assert.True(vm.IsAdvancedSection);
    }

    [Fact]
    public void SettingsViewModel_ThemeSwitching_UpdatesConfigAndTheme()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService);

        vm.SetTheme("Dark");
        Assert.Equal("Dark", vm.CurrentTheme);
        Assert.True(vm.IsDarkTheme);
        Assert.False(vm.IsLightTheme);
        Assert.Equal("Dark", config.Appearance.Theme);

        vm.SetTheme("Light");
        Assert.Equal("Light", vm.CurrentTheme);
        Assert.True(vm.IsLightTheme);
        Assert.Equal("Light", config.Appearance.Theme);
    }

    [Fact]
    public void SettingsViewModel_SecurityDynamicPassword_CalculatesDailyFormula()
    {
        var config = new AppConfig
        {
            Security = new SecuritySettings
            {
                RequireLogin = true,
                AuthMode = "DateBased",
                DatePrefix = "backup(",
                DateFormat = "ddMMyyyy",
                DateSuffix = "!)"
            }
        };

        var vm = new SettingsViewModel(config, _configService);
        vm.UpdateSecurityPreviews();

        var expectedToday = $"backup({DateTime.Today:ddMMyyyy}!)";
        var expectedTomorrow = $"backup({DateTime.Today.AddDays(1):ddMMyyyy}!)";

        Assert.Equal(expectedToday, vm.LiveTodayPasswordPreview);
        Assert.Equal(expectedTomorrow, vm.LiveTomorrowPasswordPreview);
    }

    [Fact]
    public async Task SettingsViewModel_SecurityStaticPassword_ValidatesMatching()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService)
        {
            SecurityRequireLogin = true,
            SecurityAuthMode = "Static",
            SecurityNewPassword = "SecretPassword123",
            SecurityConfirmPassword = "MismatchPassword"
        };

        await vm.SaveSecuritySettingsAsync();
        Assert.False(vm.SecurityMessageIsSuccess);
        Assert.Contains("do not match", vm.SecurityMessage);

        vm.SecurityConfirmPassword = "SecretPassword123";
        await vm.SaveSecuritySettingsAsync();
        Assert.True(vm.SecurityMessageIsSuccess);
        Assert.Equal("SecretPassword123", config.Security.AccessPassword);
    }

    [Fact]
    public void SettingsViewModel_DirtyTracking_DetectsModificationsAndResets()
    {
        var config = new AppConfig();
        config.Backup.TempFolder = @"C:\UniBackup\temp";
        var vm = new SettingsViewModel(config, _configService);

        Assert.False(vm.HasUnsavedChanges);

        config.Backup.TempFolder = @"D:\NewStagingFolder";
        vm.CheckDirty();
        Assert.True(vm.HasUnsavedChanges);

        vm.SelectSection("Storage");
        vm.ResetSection();

        Assert.Equal(@"C:\UniBackup\temp", config.Backup.TempFolder);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void SettingsViewModel_OverviewCards_ComputesLiveStatusCorrectly()
    {
        var config = new AppConfig();
        config.Aws.Enabled = true;
        config.Aws.BucketName = "my-test-bucket";
        config.Azure.Enabled = true;
        config.Azure.ContainerName = "my-container";
        config.Security.RequireLogin = true;
        config.Security.AuthMode = "DateBased";

        var vm = new SettingsViewModel(config, _configService);
        vm.RefreshOverviewCards();

        Assert.Contains("2 active destinations", vm.OverviewCloudStatus);
        Assert.Contains("Dynamic Date Formula", vm.OverviewSecurityStatus);
    }

    [Fact]
    public void SettingsViewModel_EmailToAddresses_ParsesAndFormatsCorrectly()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService);

        vm.EmailToAddressesText = "admin@uniprotech.net, alerts@uniprotech.net; devops@uniprotech.net";

        Assert.Equal(3, config.Email.ToAddresses.Count);
        Assert.Contains("admin@uniprotech.net", config.Email.ToAddresses);
        Assert.Contains("alerts@uniprotech.net", config.Email.ToAddresses);
        Assert.Contains("devops@uniprotech.net", config.Email.ToAddresses);
    }

    [Fact]
    public void SettingsViewModel_EngineBadgeAndCommands_ExposesClearStatusAndCommands()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService);

        Assert.NotNull(vm.EngineSummaryBadgeText);
        Assert.NotNull(vm.EngineSummaryBadgeBg);
        Assert.NotNull(vm.EngineSummaryBadgeBorder);
        Assert.NotNull(vm.EngineSummaryBadgeColor);
        Assert.NotNull(vm.InstallWindowsServiceCommand);
        Assert.NotNull(vm.UninstallWindowsServiceCommand);

        vm.RunByService = true;
        vm.WindowsServiceStatus = "Running";
        vm.UpdateEngineBadge();
        Assert.Equal("Engine: Service Running", vm.EngineSummaryBadgeText);

        vm.WindowsServiceStatus = "Stopped";
        vm.UpdateEngineBadge();
        Assert.Equal("Engine: Service Stopped", vm.EngineSummaryBadgeText);

        vm.WindowsServiceStatus = "Not Installed";
        vm.WindowsServiceInstalled = false;
        vm.UpdateEngineBadge();
        Assert.Equal("Engine: Not Installed", vm.EngineSummaryBadgeText);

        vm.RunByService = false;
        vm.TaskSchedulerInstalled = true;
        vm.TaskSchedulerRunning = false;
        vm.UpdateEngineBadge();
        Assert.Equal("Engine: Task Ready", vm.EngineSummaryBadgeText);
    }

    [Fact]
    public async Task SettingsViewModel_GlobalSaveSettings_PersistsSecurityAndEngineSettings()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService)
        {
            SecurityRequireLogin = true,
            SecurityAuthMode = "DateBased",
            SecurityDatePrefix = "myprefix(",
            SecurityDateFormat = "yyyyMMdd",
            SecurityDateSuffix = "!)",
            RunByService = true
        };

        await vm.SaveSettingsAsync();

        Assert.True(config.Security.RequireLogin);
        Assert.Equal("DateBased", config.Security.AuthMode);
        Assert.Equal("myprefix(", config.Security.DatePrefix);
        Assert.Equal("yyyyMMdd", config.Security.DateFormat);
        Assert.Equal("!)", config.Security.DateSuffix);
        Assert.Equal("Service", config.Schedule.Engine);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void SettingsViewModel_SecurityPropertyChanges_TriggerDynamicPreviewUpdates()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService);

        vm.SecurityDatePrefix = "corp(";
        vm.SecurityDateFormat = "ddMMyyyy";
        vm.SecurityDateSuffix = ")#";

        var expectedToday = $"corp({DateTime.Today:ddMMyyyy})#";
        Assert.Equal(expectedToday, vm.LiveTodayPasswordPreview);

        vm.SecurityRequireLogin = true;
        Assert.Equal("Password Active", vm.SecurityStatusBadgeText);

        vm.SecurityRequireLogin = false;
        Assert.Equal("Unprotected", vm.SecurityStatusBadgeText);
    }

    [Fact]
    public void SettingsViewModel_ResetAdvancedSection_RevertsAllPerformanceFields()
    {
        var config = new AppConfig();
        config.Backup.BandwidthLimitKbps = 1000;
        config.Backup.CompressionLevel = "Smallest";
        config.Backup.CleanTempAfterBackup = false;

        var vm = new SettingsViewModel(config, _configService);

        config.Backup.BandwidthLimitKbps = 5000;
        config.Backup.CompressionLevel = "None";
        config.Backup.CleanTempAfterBackup = true;

        vm.SelectSection("Advanced");
        vm.ResetSection();

        Assert.Equal(1000, config.Backup.BandwidthLimitKbps);
        Assert.Equal("Smallest", config.Backup.CompressionLevel);
        Assert.False(config.Backup.CleanTempAfterBackup);
    }

    [Fact]
    public void SettingsViewModel_ScheduleFrequencies_SwitchesModesAndUpdatesSummary()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService);

        // Daily
        vm.SetScheduleFrequency("daily");
        vm.ScheduleTime = "03:30";
        Assert.True(vm.IsScheduleDaily);
        Assert.False(vm.IsScheduleWeekly);
        Assert.Contains("03:30", vm.LiveScheduleSummary);
        Assert.Contains("every day", vm.LiveScheduleSummary);

        // Weekly
        vm.SetScheduleFrequency("weekly");
        vm.ScheduleMonday = true;
        vm.ScheduleWednesday = true;
        vm.ScheduleFriday = true;
        Assert.True(vm.IsScheduleWeekly);
        Assert.Contains("Mon", vm.LiveScheduleSummary);
        Assert.Contains("Wed", vm.LiveScheduleSummary);
        Assert.Contains("Fri", vm.LiveScheduleSummary);

        // Monthly
        vm.SetScheduleFrequency("monthly");
        vm.ScheduleDayOfMonth = "15";
        Assert.True(vm.IsScheduleMonthly);
        Assert.Contains("Day 15", vm.LiveScheduleSummary);

        // Custom
        vm.SetScheduleFrequency("custom");
        vm.ScheduleIntervalHours = 6;
        Assert.True(vm.IsScheduleCustom);
        Assert.Contains("every 6 hours", vm.LiveScheduleSummary);
    }

    [Fact]
    public async Task SettingsViewModel_SaveScheduleFrequency_PersistsToConfig()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService)
        {
            ScheduleFrequency = "weekly",
            ScheduleTime = "04:15",
            ScheduleMonday = true,
            ScheduleThursday = true,
            ScheduleWindowMode = "minimized"
        };

        await vm.SaveSettingsAsync();

        Assert.Equal("weekly", config.Schedule.Freq);
        Assert.Equal("04:15", config.Schedule.Time);
        Assert.Equal("minimized", config.Schedule.WindowMode);
        Assert.Contains("MON", config.Schedule.Days);
        Assert.Contains("THU", config.Schedule.Days);
    }

    [Fact]
    public void SettingsViewModel_ScheduleModifications_TriggerDirtyTracking()
    {
        var config = new AppConfig();
        var vm = new SettingsViewModel(config, _configService);
        Assert.False(vm.HasUnsavedChanges);

        vm.ScheduleTime = "05:45";
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal("05:45", config.Schedule.Time);

        vm.ScheduleFrequency = "weekly";
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal("weekly", config.Schedule.Freq);
    }

    [Fact]
    public void ScheduleCalculator_CustomInterval_EvaluatesDueCorrectly()
    {
        var schedule = new JobSchedule
        {
            Freq = "custom",
            Interval = 4
        };
        var now = DateTimeOffset.UtcNow;
        var runStateDue = new JobRunState { LastRunTime = now.AddHours(-5) };
        var runStateNotDue = new JobRunState { LastRunTime = now.AddHours(-2) };

        Assert.True(ScheduleCalculator.IsFolderJobDue(schedule, runStateDue, now));
        Assert.False(ScheduleCalculator.IsFolderJobDue(schedule, runStateNotDue, now));
    }
}
