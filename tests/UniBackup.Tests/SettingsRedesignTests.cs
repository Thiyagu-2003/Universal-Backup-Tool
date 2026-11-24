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

        Assert.Equal("Overview", vm.SelectedSection);
        Assert.True(vm.IsOverviewSection);
        Assert.False(vm.IsAppearanceSection);

        vm.SelectSection("Appearance");
        Assert.Equal("Appearance", vm.SelectedSection);
        Assert.True(vm.IsAppearanceSection);
        Assert.False(vm.IsOverviewSection);

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
}
