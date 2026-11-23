using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

namespace UniBackup.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ConfigurationService _configService;
    private readonly IIisDiscoveryService _iisService;
    private readonly ISchedulerService _schedulerService;
    private bool _isDarkMode;
    private RoutedEventHandler? _toastActionHandler;

    public MainWindow()
    {
        InitializeComponent();

        if (InnerLogsView?.LogViewerList != null)
        {
            RegisterName("LogViewerList", InnerLogsView.LogViewerList);
        }

        // 1. Dependency wiring
        var crypto = new CredentialProtector();
        _configService = new ConfigurationService(crypto);
        var archiveService = new HighSpeedZipArchiveService();
        var s3Storage = new AwsS3StorageProvider();
        var gdriveStorage = new GoogleDriveStorageProvider();
        var localStorage = new LocalStorageProvider();
        var azureStorage = new AzureBlobStorageProvider();
        var ftpStorage = new FtpStorageProvider();
        var sftpStorage = new SftpStorageProvider();
        var webdavStorage = new WebDavStorageProvider();
        var dropboxStorage = new DropboxStorageProvider();
        var onedriveStorage = new OneDriveStorageProvider();
        var boxStorage = new BoxStorageProvider();
        var storageProviders = new IStorageProvider[] { s3Storage, gdriveStorage, localStorage, azureStorage, ftpStorage, sftpStorage, webdavStorage, dropboxStorage, onedriveStorage, boxStorage };

        var emailService = new EmailNotificationService();
        var webhookService = new WebhookNotificationService();
        _schedulerService = new WinTaskSchedulerService();
        _iisService = new IisSiteDiscoveryService();

        var sqlBackup = new SqlServerBackupService(crypto);
        var pipeline = new BackupPipeline(archiveService, storageProviders, emailService, webhookService, _configService, sqlBackup);
        var batchRunner = new BatchBackupRunner(pipeline, _configService, emailService);
        var recoveryService = new RecoveryService(storageProviders, archiveService);
        var diagnostics = new ServerDiagnosticsService(storageProviders, emailService, _iisService, _configService);

        var sqlRestore = new SqlServerRestoreService(crypto);
        var sqlOrchestrator = new SqlRestoreOrchestrator(storageProviders, sqlRestore);
        var restoreDrill = new RestoreDrillService(sqlOrchestrator, sqlBackup);

        _viewModel = new MainViewModel(
            _configService,
            pipeline,
            batchRunner,
            recoveryService,
            diagnostics,
            _schedulerService,
            _iisService,
            emailService,
            sqlOrchestrator,
            restoreDrill);

        DataContext = _viewModel;
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab))
            {
                Dispatcher.InvokeAsync(() => SwitchToTab(_viewModel.ActiveTab));
            }
        };

        NotificationHub.ToastRequested += (_, toast) =>
        {
            Dispatcher.InvokeAsync(() => ShowToastUi(toast));
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    // Titlebar Caption Buttons
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private bool _isSwitchingTab;
    public void SwitchToTab(string tag)
    {
        if (_isSwitchingTab) return;
        _isSwitchingTab = true;
        try
        {
            if (OverviewTabContent == null || BackupsTabContent == null || NewJobTabContent == null ||
                IisSitesTabContent == null || RecoveryTabContent == null || SummaryTabContent == null ||
                LogsTabContent == null || NotificationsTabContent == null || SettingsTabContent == null) return;

            // Animate view transition (fade + translateY 8 -> 0 over 320 ms EaseOut, §2.4)
            UIElement? targetElement = tag switch
            {
                "Overview" => OverviewTabContent,
                "Backups" => BackupsTabContent,
                "NewJob" => NewJobTabContent,
                "IisSites" => IisSitesTabContent,
                "Recovery" => RecoveryTabContent,
                "Summary" => SummaryTabContent,
                "Logs" => LogsTabContent,
                "Notifications" => NotificationsTabContent,
                "Settings" => SettingsTabContent,
                _ => BackupsTabContent
            };

            OverviewTabContent.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            BackupsTabContent.Visibility = tag == "Backups" ? Visibility.Visible : Visibility.Collapsed;
            NewJobTabContent.Visibility = tag == "NewJob" ? Visibility.Visible : Visibility.Collapsed;
            IisSitesTabContent.Visibility = tag == "IisSites" ? Visibility.Visible : Visibility.Collapsed;
            RecoveryTabContent.Visibility = tag == "Recovery" ? Visibility.Visible : Visibility.Collapsed;
            SummaryTabContent.Visibility = tag == "Summary" ? Visibility.Visible : Visibility.Collapsed;
            LogsTabContent.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
            NotificationsTabContent.Visibility = tag == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
            SettingsTabContent.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

            _viewModel.ActiveTab = tag;

            if (NavOverviewRadio != null && tag == "Overview") NavOverviewRadio.IsChecked = true;
            if (NavBackupsRadio != null && tag == "Backups") NavBackupsRadio.IsChecked = true;
            if (NavNewJobRadio != null && tag == "NewJob") NavNewJobRadio.IsChecked = true;
            if (NavIisSitesRadio != null && tag == "IisSites") NavIisSitesRadio.IsChecked = true;
            if (NavRecoveryRadio != null && tag == "Recovery") NavRecoveryRadio.IsChecked = true;
            if (NavSummaryRadio != null && tag == "Summary") NavSummaryRadio.IsChecked = true;
            if (NavLogsRadio != null && tag == "Logs") NavLogsRadio.IsChecked = true;
            if (NavNotificationsRadio != null && tag == "Notifications") NavNotificationsRadio.IsChecked = true;
            if (NavSettingsRadio != null && tag == "Settings") NavSettingsRadio.IsChecked = true;

            if (tag == "NewJob")
            {
                if (_viewModel.NewJob == null)
                {
                    _viewModel.OpenNewJob();
                }
                if (_viewModel.NewJob != null)
                {
                    _viewModel.NewJob.CloseAction = () => SwitchToTab("Backups");
                }
            }
            else if (tag == "Recovery" && _viewModel.CloudArchives.Count == 0)
            {
                _ = _viewModel.RefreshRecoveryCommand.ExecuteAsync(null);
            }
            else if (tag == "Logs")
            {
                _ = _viewModel.Logs.RefreshLogsCommand.ExecuteAsync(null);
            }

            if (targetElement != null && SystemParameters.ClientAreaAnimation)
            {
                var fadeIn = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(320)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                targetElement.BeginAnimation(OpacityProperty, fadeIn);
            }
        }
        finally
        {
            _isSwitchingTab = false;
        }
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        SwitchToTab(tag);
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SearchQuery = string.Empty;
        if (MainSearchBox != null)
        {
            MainSearchBox.Text = string.Empty;
            MainSearchBox.Focus();
        }
    }

    private void NewBackup_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenNewJob();
        if (_viewModel.NewJob != null)
        {
            _viewModel.NewJob.CloseAction = () => SwitchToTab("Backups");
        }
        SwitchToTab("NewJob");
    }

    private void ValidateHealth_Click(object sender, RoutedEventArgs e)
    {
        SwitchToTab("Settings");
        _ = _viewModel.RunValidateServerCommand.ExecuteAsync(null);
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        App.ApplyTheme(_isDarkMode ? "Dark" : "Light");
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;

        // Responsive Breakpoints (§7)
        if (w < 1080)
        {
            // Rail collapses to 72 px icons
            if (NavRailColumn != null) NavRailColumn.Width = new GridLength(72);
        }
        else
        {
            if (NavRailColumn != null) NavRailColumn.Width = new GridLength(244);
        }
    }

    // ===================================================================
    // TOAST NOTIFICATIONS (§2.4, §6)
    // ===================================================================
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    private void ShowToastUi(ToastNotification toast)
    {
        if (ToastNotificationCard == null) return;

        switch (toast.Type)
        {
            case ToastType.Success:
                ToastIconBd.Background = TryFindResource("BrushJadeSoft") as Brush ?? Brushes.LightGreen;
                ToastIconPath.Fill = TryFindResource("BrushJade") as Brush ?? Brushes.Green;
                ToastIconPath.Data = (Geometry)FindResource("IconCheckCircle");
                break;
            case ToastType.Info:
                ToastIconBd.Background = TryFindResource("BrushCobaltSoft") as Brush ?? Brushes.LightBlue;
                ToastIconPath.Fill = TryFindResource("BrushCobalt") as Brush ?? Brushes.Blue;
                ToastIconPath.Data = (Geometry)FindResource("IconInfoCircle");
                break;
            case ToastType.Warning:
                ToastIconBd.Background = TryFindResource("BrushSaffronSoft") as Brush ?? Brushes.LightYellow;
                ToastIconPath.Fill = TryFindResource("BrushSaffron") as Brush ?? Brushes.Orange;
                ToastIconPath.Data = (Geometry)FindResource("IconWarningCircle");
                break;
            case ToastType.Error:
                ToastIconBd.Background = TryFindResource("BrushCoralSoft") as Brush ?? Brushes.Pink;
                ToastIconPath.Fill = TryFindResource("BrushCoral") as Brush ?? Brushes.Red;
                ToastIconPath.Data = (Geometry)FindResource("IconCloseCircle");
                break;
        }

        ToastTitleText.Text = toast.Title;
        ToastMessageText.Text = toast.Message;

        if (ToastActionButton != null)
        {
            if (_toastActionHandler != null)
            {
                ToastActionButton.Click -= _toastActionHandler;
                _toastActionHandler = null;
            }

            if (toast.HasAction)
            {
                ToastActionButton.Visibility = Visibility.Visible;
                ToastActionButton.Content = toast.ActionLabel;
                _toastActionHandler = (_, _) =>
                {
                    toast.Action?.Invoke();
                    DismissToast();
                };
                ToastActionButton.Click += _toastActionHandler;
            }
            else
            {
                ToastActionButton.Visibility = Visibility.Collapsed;
            }
        }

        _toastTimer?.Stop();
        ToastNotificationCard.BeginAnimation(UIElement.OpacityProperty, null);
        ToastTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ToastNotificationCard.Visibility = Visibility.Visible;

        if (SystemParameters.ClientAreaAnimation)
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));
            var slideIn = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };

            ToastNotificationCard.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ToastTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);
        }
        else
        {
            ToastNotificationCard.Opacity = 1;
            ToastTransform.Y = 0;
        }

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

        if (SystemParameters.ClientAreaAnimation)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            var slideOut = new DoubleAnimation(0, 20, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                if (ToastNotificationCard.Opacity <= 0.05)
                {
                    ToastNotificationCard.Visibility = Visibility.Collapsed;
                }
            };

            ToastNotificationCard.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ToastTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);
        }
        else
        {
            ToastNotificationCard.Visibility = Visibility.Collapsed;
        }
    }
}
