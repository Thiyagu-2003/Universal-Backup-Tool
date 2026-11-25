using System.IO;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.Infrastructure.Windows;
using UniBackup.UI.ViewModels;
using Xunit;

namespace UniBackup.Tests;

public class NotificationsRedesignTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigurationService _configService;
    private readonly MainViewModel _vm;

    public NotificationsRedesignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "unibackup_notif_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var crypto = new CredentialProtector();
        _configService = new ConfigurationService(crypto, _tempDir);

        var archiveService = new HighSpeedZipArchiveService();
        var storageProviders = new IStorageProvider[] { new LocalStorageProvider() };
        var emailService = new EmailNotificationService();
        var webhookService = new WebhookNotificationService();
        var schedulerService = new WinTaskSchedulerService();
        var iisService = new IisSiteDiscoveryService();
        var sqlBackup = new SqlServerBackupService(crypto);
        var pipeline = new BackupPipeline(archiveService, storageProviders, emailService, webhookService, _configService, sqlBackup);
        var batchRunner = new BatchBackupRunner(pipeline, _configService, emailService);
        var recoveryService = new RecoveryService(storageProviders, archiveService);
        var diagnostics = new ServerDiagnosticsService(storageProviders, emailService, iisService, _configService);

        _vm = new MainViewModel(
            _configService,
            pipeline,
            batchRunner,
            recoveryService,
            diagnostics,
            schedulerService,
            iisService,
            emailService);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ProviderSelection_UpdatesActiveFlagsAcrossAllProviders()
    {
        string[] providers =
        [
            "Email", "Slack", "Teams", "Discord", "Telegram",
            "Pushover", "Ntfy", "PagerDuty", "CustomWebhook", "Heartbeat"
        ];

        foreach (var p in providers)
        {
            _vm.SelectNotificationProvider(p);
            Assert.Equal(p, _vm.SelectedNotificationProvider);
        }

        _vm.SelectNotificationProvider("Telegram");
        Assert.True(_vm.IsProviderTelegram);
        Assert.False(_vm.IsProviderEmail);
    }

    [Fact]
    public void SummaryCounts_ReflectConfiguredChannelsAccurately()
    {
        _vm.Config.Email.Enabled = true;
        _vm.Config.Email.SmtpHost = "smtp.example.com";
        _vm.Config.Webhooks.SlackWebhookUrl = "https://hooks.slack.com/services/test";
        _vm.Config.Heartbeat.Enabled = true;
        _vm.Config.Heartbeat.Url = "https://hc-ping.com/123";

        _vm.RefreshNotificationSummary();

        Assert.Equal(3, _vm.ConfiguredChannelsCount);
        Assert.Equal(3, _vm.ActiveChannelsCount);
    }

    [Fact]
    public void NotificationMatrixColumnToggles_UpdateAllRowsInColumn()
    {
        _vm.Config.NotificationMatrix.FullSuccess = false;
        _vm.Config.NotificationMatrix.DiffSuccess = false;
        _vm.Config.NotificationMatrix.LogSuccess = false;

        _vm.ToggleAllSuccessNotifications();
        Assert.True(_vm.Config.NotificationMatrix.FullSuccess);
        Assert.True(_vm.Config.NotificationMatrix.DiffSuccess);
        Assert.True(_vm.Config.NotificationMatrix.LogSuccess);
        Assert.True(_vm.HasUnsavedNotificationChanges);

        _vm.ToggleAllSuccessNotifications();
        Assert.False(_vm.Config.NotificationMatrix.FullSuccess);
        Assert.False(_vm.Config.NotificationMatrix.DiffSuccess);
        Assert.False(_vm.Config.NotificationMatrix.LogSuccess);
    }

    [Fact]
    public void NotificationActivity_RecordsFiltersAndPaginates()
    {
        _vm.RecordNotificationDispatch("Email", "Test Dispatch", true, null, "SQL Prod");
        _vm.RecordNotificationDispatch("Slack", "Backup Alert", false, "HTTP 500", "IIS Portal");
        _vm.RecordNotificationDispatch("Telegram", "Test Dispatch", true, null, "-");

        Assert.Equal(1, _vm.FailedTestNotificationsCount);
        Assert.True(_vm.HasNotificationActivity);
        Assert.Equal(3, _vm.NotificationActivityPaged.Count);

        _vm.NotificationProviderFilter = "Slack";
        Assert.Single(_vm.NotificationActivityPaged);
        Assert.Equal("Slack", _vm.NotificationActivityPaged[0].Provider);

        _vm.NotificationProviderFilter = "All Providers";
        _vm.NotificationStatusFilter = "Failed";
        Assert.Single(_vm.NotificationActivityPaged);
        Assert.Equal("HTTP 500", _vm.NotificationActivityPaged[0].ErrorDetails);

        _vm.ViewNotificationActivityDetails(_vm.NotificationActivityPaged[0]);
        Assert.True(_vm.IsNotificationActivityDetailsOpen);
        _vm.CloseNotificationActivityDetails();
        Assert.False(_vm.IsNotificationActivityDetailsOpen);
    }
}
