using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UniBackup.Core.Interfaces;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.Infrastructure.Windows;
using UniBackup.UI.Services;
using UniBackup.UI.ViewModels;
using UniBackup.UI.Views;

namespace UniBackup.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ConfigurationService _configService;
    private readonly IIisDiscoveryService _iisService;
    private readonly ISchedulerService _schedulerService;

    public MainWindow()
    {
        InitializeComponent();

        // 1. Dependency wiring
        var crypto = new CredentialProtector();
        _configService = new ConfigurationService(crypto);
        var archiveService = new HighSpeedZipArchiveService();
        var s3Storage = new AwsS3StorageProvider();
        var gdriveStorage = new GoogleDriveStorageProvider();
        var storageProviders = new IStorageProvider[] { s3Storage, gdriveStorage };

        var emailService = new EmailNotificationService();
        var webhookService = new WebhookNotificationService();
        _schedulerService = new WinTaskSchedulerService();
        _iisService = new IisSiteDiscoveryService();

        var pipeline = new BackupPipeline(archiveService, storageProviders, emailService, webhookService, _configService, new SqlServerBackupService(crypto));
        var batchRunner = new BatchBackupRunner(pipeline, _configService, emailService);
        var recoveryService = new RecoveryService(storageProviders, archiveService);
        var diagnostics = new ServerDiagnosticsService(storageProviders, emailService, _iisService, _configService);

        _viewModel = new MainViewModel(
            _configService,
            pipeline,
            batchRunner,
            recoveryService,
            diagnostics,
            _schedulerService,
            _iisService,
            emailService);

        DataContext = _viewModel;
        _viewModel.Logs.ScrollRequested += (_, line) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (LogViewerList != null)
                {
                    LogViewerList.ScrollIntoView(line);
                    LogViewerList.SelectedItem = line;
                }
            });
        };

        NotificationHub.ToastRequested += (_, toast) =>
        {
            Dispatcher.InvokeAsync(() => ShowToastUi(toast));
        };

        Loaded += MainWindow_Loaded;
    }

    private bool _syncingSmtpPassword;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        SyncSmtpPassword();
        SyncAwsSecret();
        _viewModel.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsSmtpPasswordVisible) || args.PropertyName == nameof(MainViewModel.Config))
            {
                SyncSmtpPassword();
            }
            if (args.PropertyName == nameof(MainViewModel.IsAwsSecretVisible) || args.PropertyName == nameof(MainViewModel.Config))
            {
                SyncAwsSecret();
            }
        };
    }

    private void SyncSmtpPassword()
    {
        if (_syncingSmtpPassword || SmtpPasswordInput == null || _viewModel.Config?.Email == null) return;
        _syncingSmtpPassword = true;
        try
        {
            var pwd = _viewModel.Config.Email.Password ?? "";
            if (SmtpPasswordInput.Password != pwd)
            {
                SmtpPasswordInput.Password = pwd;
            }
        }
        finally
        {
            _syncingSmtpPassword = false;
        }
    }

    private void SmtpPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSmtpPassword || _viewModel?.Config?.Email == null) return;
        _syncingSmtpPassword = true;
        try
        {
            _viewModel.Config.Email.Password = SmtpPasswordInput.Password;
        }
        finally
        {
            _syncingSmtpPassword = false;
        }
    }

    private bool _syncingAwsSecret;

    private void SyncAwsSecret()
    {
        if (_syncingAwsSecret || AwsSecretKeyInput == null || _viewModel.Config?.Aws == null) return;
        _syncingAwsSecret = true;
        try
        {
            var secret = _viewModel.Config.Aws.SecretAccessKey ?? "";
            if (AwsSecretKeyInput.Password != secret)
            {
                AwsSecretKeyInput.Password = secret;
            }
        }
        finally
        {
            _syncingAwsSecret = false;
        }
    }

    private void AwsSecretKeyInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingAwsSecret || _viewModel?.Config?.Aws == null) return;
        _syncingAwsSecret = true;
        try
        {
            _viewModel.Config.Aws.SecretAccessKey = AwsSecretKeyInput.Password;
        }
        finally
        {
            _syncingAwsSecret = false;
        }
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        if (BackupsTabContent == null || SummaryTabContent == null ||
            RecoveryTabContent == null || LogsTabContent == null || SettingsTabContent == null) return;

        BackupsTabContent.Visibility = tag == "Backups" ? Visibility.Visible : Visibility.Collapsed;
        SummaryTabContent.Visibility = tag == "Summary" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryTabContent.Visibility = tag == "Recovery" ? Visibility.Visible : Visibility.Collapsed;
        LogsTabContent.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabContent.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        _viewModel.ActiveTab = tag;

        if (tag == "Recovery" && _viewModel.CloudArchives.Count == 0)
        {
            _ = _viewModel.RefreshRecoveryCommand.ExecuteAsync(null);
        }
        else if (tag == "Logs")
        {
            _ = _viewModel.Logs.RefreshLogsCommand.ExecuteAsync(null);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = _viewModel.RefreshJobsCommand.ExecuteAsync(null);
    }

    private void NewBackup_Click(object sender, RoutedEventArgs e)
    {
        var newVm = new NewBackupViewModel(_configService, _iisService, _schedulerService);
        var dlg = new NewBackupDialog(newVm) { Owner = this };
        dlg.ShowDialog();

        if (newVm.DialogResult)
        {
            _ = _viewModel.RefreshJobsCommand.ExecuteAsync(null);
        }
    }

    private void BrowseRestoreDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Destination Folder for Restore"
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.RestoreDestinationPath = dialog.FolderName;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    private void ShowToastUi(ToastNotification toast)
    {
        if (ToastNotificationCard == null) return;

        switch (toast.Type)
        {
            case ToastType.Success:
                ToastIconBd.Background = new SolidColorBrush(Color.FromRgb(6, 78, 59));
                ToastIconPath.Fill = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                ToastIconPath.Data = (Geometry)FindResource("IconCheckCircle");
                break;
            case ToastType.Info:
                ToastIconBd.Background = new SolidColorBrush(Color.FromRgb(30, 58, 138));
                ToastIconPath.Fill = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                ToastIconPath.Data = (Geometry)FindResource("IconInfoCircle");
                break;
            case ToastType.Warning:
                ToastIconBd.Background = new SolidColorBrush(Color.FromRgb(120, 53, 15));
                ToastIconPath.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                ToastIconPath.Data = (Geometry)FindResource("IconWarningCircle");
                break;
            case ToastType.Error:
                ToastIconBd.Background = new SolidColorBrush(Color.FromRgb(136, 19, 55));
                ToastIconPath.Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                ToastIconPath.Data = (Geometry)FindResource("IconCloseCircle");
                break;
        }

        ToastTitleText.Text = toast.Title;
        ToastMessageText.Text = toast.Message;

        _toastTimer?.Stop();
        ToastNotificationCard.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        var slideIn = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        ToastNotificationCard.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        ToastTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);

        _toastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = toast.Duration
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            DismissToast();
        };
        _toastTimer.Start();
    }

    private void DismissToast_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        DismissToast();
    }

    private void DismissToast()
    {
        if (ToastNotificationCard == null || ToastNotificationCard.Visibility != Visibility.Visible) return;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        var slideOut = new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            ToastNotificationCard.Visibility = Visibility.Collapsed;
        };

        ToastNotificationCard.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        ToastTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }
}
