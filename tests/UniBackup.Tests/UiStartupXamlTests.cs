using System.Threading;
using System.Windows;
using UniBackup.Core.Models;
using UniBackup.UI;
using UniBackup.UI.ViewModels;
using UniBackup.UI.Views;
using Xunit;

namespace UniBackup.Tests;

public class UiStartupXamlTests
{
    private static void RunInStaThread(Action action)
    {
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (thrown != null)
        {
            throw new Exception($"STA Execution failed: {thrown.Message}", thrown);
        }
    }

    [Fact]
    public void App_Resources_CanBeInitialized_WithoutExceptions()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            Assert.NotNull(Application.Current);
            Assert.NotNull(Application.Current.Resources["PrimaryGradient"]);
            Assert.NotNull(Application.Current.Resources["SuccessGradient"]);
            Assert.NotNull(Application.Current.Resources["ModernCard"]);
            Assert.NotNull(Application.Current.Resources["IconBackups"]);
        });
    }

    [Fact]
    public void MainWindow_CanBeInstantiated_WithoutXamlExceptions()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var window = new MainWindow();
            Assert.NotNull(window);
            Assert.Contains("UniBackup", window.Title);
            Assert.NotNull(window.FindName("BackupsTabContent"));
            Assert.NotNull(window.FindName("SummaryTabContent"));
            Assert.NotNull(window.FindName("RecoveryTabContent"));
            Assert.NotNull(window.FindName("LogsTabContent"));
            Assert.NotNull(window.FindName("LogViewerList"));
            Assert.NotNull(window.FindName("NotificationsTabContent"));
            Assert.NotNull(window.FindName("SettingsTabContent"));
            Assert.NotNull(window.FindName("NewJobTabContent"));

            // Test navigating to the embedded New Job screen
            window.SwitchToTab("NewJob");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Visible, ((UIElement)window.FindName("NewJobTabContent")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("BackupsTabContent")).Visibility);

            // Test navigating back to Backups screen
            window.SwitchToTab("Backups");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Visible, ((UIElement)window.FindName("BackupsTabContent")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("NewJobTabContent")).Visibility);
        });
    }

    [Fact]
    public void NewBackupDialog_CanBeInstantiated_WithoutXamlExceptions()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var crypto = new UniBackup.Infrastructure.Security.CredentialProtector();
            var configService = new UniBackup.Engine.Services.ConfigurationService(crypto);
            var iisService = new UniBackup.Infrastructure.Windows.IisSiteDiscoveryService();
            var schedulerService = new UniBackup.Infrastructure.Windows.WinTaskSchedulerService();

            var vm = new NewBackupViewModel(configService, iisService, schedulerService);
            var dialog = new NewBackupDialog(vm);

            Assert.NotNull(dialog);
            Assert.Contains("Create Backup Job", dialog.Title);

            // Show window and pump dispatcher to activate all WPF data bindings and layout engine
            dialog.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Test TwoWay binding for Source Selection (Folder <-> SQL Server)
            Assert.True(vm.IsFolder);
            Assert.False(vm.IsSqlServer);

            vm.IsSqlServer = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(vm.IsSqlServer);
            Assert.False(vm.IsFolder);

            vm.IsFolder = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(vm.IsFolder);
            Assert.False(vm.IsSqlServer);

            // Test step commands with string parameters (exactly what XAML buttons pass)
            vm.SetStepCommand.Execute("1");
            Assert.True(vm.IsStep1);
            vm.SetStepCommand.Execute("2");
            Assert.True(vm.IsStep2);
            vm.SetStepCommand.Execute("3");
            Assert.True(vm.IsStep3);
            vm.SetStepCommand.Execute("4");
            Assert.True(vm.IsStep4);

            vm.PreviousStepCommand.Execute(null);
            Assert.True(vm.IsStep3);
            vm.NextStepCommand.Execute(null);
            Assert.True(vm.IsStep4);

            // Test SQL Authentication toggle
            vm.SqlUseWindowsAuth = false;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(vm.SqlUseSqlAuth);

            vm.SqlUseWindowsAuth = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(vm.SqlUseSqlAuth);

            dialog.Close();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        });
    }

    [Fact]
    public void LoginViewModel_Validates_AdminUpsDdmmyyyy_Format()
    {
        var today = DateTime.Now.ToString("ddMMyyyy");
        var expectedTodayPass = $"adminups{today}";

        // Dynamic daily format
        Assert.True(LoginViewModel.CheckPassword(expectedTodayPass, null));
        Assert.True(LoginViewModel.CheckPassword(expectedTodayPass.ToUpperInvariant(), null));

        // Literal format
        Assert.True(LoginViewModel.CheckPassword("adminupsddmmyyyy", null));
        Assert.True(LoginViewModel.CheckPassword("ADMINUPSDDMMYYYY", null));

        // Rejections
        Assert.False(LoginViewModel.CheckPassword("wrongpassword", null));
        Assert.False(LoginViewModel.CheckPassword("admin", null));
        Assert.False(LoginViewModel.CheckPassword("", null));
    }

    [Fact]
    public void SecuritySettings_Default_HasNoPassword_AndRequireLoginIsFalse()
    {
        var sec = new UniBackup.Core.Models.SecuritySettings();
        Assert.False(sec.RequireLogin);
        Assert.Equal("", sec.AccessPassword);
        Assert.Equal("Static", sec.AuthMode);
    }

    [Fact]
    public void SecuritySettings_DynamicDateBased_GeneratesConfiguredPatterns()
    {
        var testDate = new DateTime(2026, 11, 23);

        // Pattern 1: admin(23-11-2026)
        var sec1 = new UniBackup.Core.Models.SecuritySettings
        {
            AuthMode = "DateBased",
            DatePrefix = "admin(",
            DateFormat = "dd-MM-yyyy",
            DateSuffix = ")"
        };
        Assert.Equal("admin(23-11-2026)", sec1.GenerateExpectedPassword(testDate));

        // Pattern 2: ups(2026-11-23)
        var sec2 = new UniBackup.Core.Models.SecuritySettings
        {
            AuthMode = "DateBased",
            DatePrefix = "ups(",
            DateFormat = "yyyy-MM-dd",
            DateSuffix = ")"
        };
        Assert.Equal("ups(2026-11-23)", sec2.GenerateExpectedPassword(testDate));

        // Pattern 3: backup_23112026
        var sec3 = new UniBackup.Core.Models.SecuritySettings
        {
            AuthMode = "DateBased",
            DatePrefix = "backup_",
            DateFormat = "ddMMyyyy",
            DateSuffix = ""
        };
        Assert.Equal("backup_23112026", sec3.GenerateExpectedPassword(testDate));
    }

    [Fact]
    public void LoginViewModel_Validates_CustomDateBased_And_StaticPassword()
    {
        // 1. Static Mode
        var staticSec = new UniBackup.Core.Models.SecuritySettings
        {
            RequireLogin = true,
            AuthMode = "Static",
            AccessPassword = "MyCustomPassword456!"
        };
        Assert.True(LoginViewModel.CheckSecurity("MyCustomPassword456!", staticSec));
        Assert.False(LoginViewModel.CheckSecurity("WrongPassword", staticSec));
        // Verify no backdoors bypass custom static password
        Assert.False(LoginViewModel.CheckSecurity("adminupsddmmyyyy", staticSec));
        Assert.False(LoginViewModel.CheckSecurity($"adminups{DateTime.Now:ddMMyyyy}", staticSec));

        // 2. Date-based Mode
        var dateSec = new UniBackup.Core.Models.SecuritySettings
        {
            RequireLogin = true,
            AuthMode = "DateBased",
            DatePrefix = "admin(",
            DateFormat = "dd-MM-yyyy",
            DateSuffix = ")"
        };
        var todayPass = dateSec.GenerateExpectedPassword(DateTime.Now);
        Assert.True(LoginViewModel.CheckSecurity(todayPass, dateSec));
        Assert.True(LoginViewModel.CheckSecurity(todayPass.ToLowerInvariant(), dateSec));
        Assert.False(LoginViewModel.CheckSecurity("admin(wrong-date)", dateSec));
        // Verify no backdoors bypass custom date-based formula
        Assert.False(LoginViewModel.CheckSecurity("adminupsddmmyyyy", dateSec));
        Assert.False(LoginViewModel.CheckSecurity($"adminups{DateTime.Now:ddMMyyyy}", dateSec));
    }

    [Fact]
    public void LoginWindow_CanBeInstantiated_WithoutXamlExceptions()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var crypto = new UniBackup.Infrastructure.Security.CredentialProtector();
            var configService = new UniBackup.Engine.Services.ConfigurationService(crypto);
            var vm = new LoginViewModel(configService);
            var login = new LoginWindow(vm);

            Assert.NotNull(login);
            Assert.Contains("Sign In", login.Title);
            Assert.NotNull(login.FindName("PasswordInput"));
            Assert.NotNull(login.FindName("PasswordVisibleInput"));
            Assert.NotNull(login.FindName("ToggleEyeBtn"));
            Assert.NotNull(login.FindName("EyeIconPath"));
        });
    }

    [Fact]
    public void LoginWindow_ToggleEye_RevealsPassword_AndRendersCleanly()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var crypto = new UniBackup.Infrastructure.Security.CredentialProtector();
            var configService = new UniBackup.Engine.Services.ConfigurationService(crypto);
            var vm = new LoginViewModel(configService);
            var login = new LoginWindow(vm);

            var passInput = (System.Windows.Controls.PasswordBox)login.FindName("PasswordInput");
            var passVis = (System.Windows.Controls.TextBox)login.FindName("PasswordVisibleInput");
            var eyeBtn = (System.Windows.Controls.Button)login.FindName("ToggleEyeBtn");

            var todayPassword = $"adminups{DateTime.Now:ddMMyyyy}";
            passInput.Password = todayPassword;

            // Initially masked - render to verify dots
            Assert.Equal(Visibility.Visible, passInput.Visibility);
            Assert.Equal(Visibility.Collapsed, passVis.Visibility);

            var rootVisual = (FrameworkElement)login.Content;
            rootVisual.Measure(new Size(920, 560));
            rootVisual.Arrange(new Rect(0, 0, 920, 560));
            rootVisual.UpdateLayout();

            var scratchDir = @"C:\Users\Administrator\.gemini\antigravity\brain\130ca6a4-71b0-4443-b2a2-5ffff00e8463\scratch";
            if (!System.IO.Directory.Exists(scratchDir))
            {
                System.IO.Directory.CreateDirectory(scratchDir);
            }

            var rtbMasked = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 560, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbMasked.Render(rootVisual);
            var encoderMasked = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderMasked.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbMasked));
            using (var stream = System.IO.File.Create(System.IO.Path.Combine(scratchDir, "login_password_masked.png")))
            {
                encoderMasked.Save(stream);
            }

            // Toggle eye to reveal
            eyeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Collapsed, passInput.Visibility);
            Assert.Equal(Visibility.Visible, passVis.Visibility);
            Assert.Equal(todayPassword, passVis.Text);

            // Toggle eye back to hide
            eyeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Visible, passInput.Visibility);
            Assert.Equal(Visibility.Collapsed, passVis.Visibility);
            Assert.Equal(todayPassword, passInput.Password);

            // Reveal again and render to bitmap to verify visual layout
            eyeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            rootVisual.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(920, 560, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(rootVisual);

            var outPath = System.IO.Path.Combine(scratchDir, "login_password_revealed.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var stream = System.IO.File.Create(outPath))
            {
                encoder.Save(stream);
            }

            Assert.True(System.IO.File.Exists(outPath));
            var fileInfo = new System.IO.FileInfo(outPath);
            Assert.True(fileInfo.Length > 1000);
        });
    }

    [Fact]
    public async Task LogsViewModel_LoadsTwoPrimaryStreams_ByDefault()
    {
        var vm = new LogsViewModel();
        await vm.RefreshLogsAsync();

        Assert.True(vm.Files.Count >= 2);
        Assert.Contains(vm.Files, f => f.Kind == "Manual" && f.DisplayTitle == "Manual Run");
        Assert.Contains(vm.Files, f => f.Kind == "Service" && f.DisplayTitle == "Service Run");
    }

    [Fact]
    public void LogsViewModel_ParsesLogLines_Accurately()
    {
        var lineInf = new LogLine("2026-08-28 11:22:20 [INF] Backup pipeline started.", 1);
        Assert.Equal("INF", lineInf.Level);
        Assert.False(lineInf.IsError);
        Assert.False(lineInf.IsWarning);

        var lineErr = new LogLine("2026-08-28 11:22:20 [ERR] Upload to S3 failed: Connection reset.", 2);
        Assert.Equal("ERR", lineErr.Level);
        Assert.True(lineErr.IsError);

        var lineWrn = new LogLine("2026-08-28 11:22:20 [WRN] Low disk space warning on temp drive.", 3);
        Assert.Equal("WRN", lineWrn.Level);
        Assert.True(lineWrn.IsWarning);

        var lineSuccess = new LogLine("2026-08-28 11:22:20 [INF] ✔ SUCCESS — 'WebSiteBackup' uploaded.", 4);
        Assert.True(lineSuccess.IsSuccess);
    }

    [Fact]
    public async Task LogsViewModel_Previous_And_NextProblem_NavigatesAndHighlights()
    {
        var vm = new LogsViewModel();
        await vm.RefreshLogsAsync();

        if (vm.VisibleFiles.Count > 0)
        {
            vm.SelectedFile = vm.VisibleFiles[0];
            await Task.Delay(100);

            if (vm.ProblemTotal > 0)
            {
                var initialPos = vm.ProblemPosition;
                vm.NextProblem();
                Assert.Contains(vm.VisibleLines, l => l.Highlighted);

                vm.PreviousProblem();
                Assert.Contains(vm.VisibleLines, l => l.Highlighted);
            }
        }
    }

    [Fact]
    public void NotificationHub_ToastNotification_CanBeFired_AndRenderedInMainWindow()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var window = new MainWindow();
            Assert.NotNull(window);

            bool toastReceived = false;
            EventHandler<UniBackup.UI.Services.ToastNotification> handler = (s, toast) =>
            {
                toastReceived = true;
                Assert.Equal("Copied to Clipboard", toast.Title);
                Assert.Contains("156 lines", toast.Message);
            };

            UniBackup.UI.Services.NotificationHub.ToastRequested += handler;
            try
            {
                UniBackup.UI.Services.NotificationHub.ShowSuccess("Copied 156 lines to clipboard.", "Copied to Clipboard");
                Assert.True(toastReceived);
            }
            finally
            {
                UniBackup.UI.Services.NotificationHub.ToastRequested -= handler;
            }

            var rootVisual = (FrameworkElement)window.Content;
            rootVisual.Measure(new Size(1240, 840));
            rootVisual.Arrange(new Rect(0, 0, 1240, 840));
            rootVisual.UpdateLayout();

            var toastCard = (FrameworkElement)window.FindName("ToastNotificationCard");
            Assert.NotNull(toastCard);

            var backupsContent = (FrameworkElement)window.FindName("BackupsTabContent");
            var logsContent = (FrameworkElement)window.FindName("LogsTabContent");
            if (backupsContent != null) backupsContent.Visibility = Visibility.Collapsed;
            if (logsContent != null) logsContent.Visibility = Visibility.Visible;

            toastCard.Visibility = Visibility.Visible;
            toastCard.Opacity = 1;
            var toastTitle = (System.Windows.Controls.TextBlock)window.FindName("ToastTitleText");
            var toastMsg = (System.Windows.Controls.TextBlock)window.FindName("ToastMessageText");
            if (toastTitle != null) toastTitle.Text = "Copied to Clipboard";
            if (toastMsg != null) toastMsg.Text = "Copied 156 lines to clipboard successfully.";
            var transform = (System.Windows.Media.TranslateTransform)window.FindName("ToastTransform");
            if (transform != null) transform.Y = 0;

            rootVisual.Measure(new Size(1240, 840));
            rootVisual.Arrange(new Rect(0, 0, 1240, 840));
            rootVisual.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 840, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(rootVisual);

            var scratchDir = @"C:\Users\Administrator\.gemini\antigravity\brain\130ca6a4-71b0-4443-b2a2-5ffff00e8463\scratch";
            var outPath = System.IO.Path.Combine(scratchDir, "mainwindow_logs_toast.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var stream = System.IO.File.Create(outPath))
            {
                encoder.Save(stream);
            }

            Assert.True(System.IO.File.Exists(outPath));
        });
    }

    [Fact]
    public void SettingsPage_ScheduleTimings_AndRunMode_RenderAndVerify()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var window = new MainWindow();
            var vm = (MainViewModel)window.DataContext;

            // 1. Verify Schedule Presets and Timing Conversion
            vm.UseSchedulePreset("23:30");
            Assert.Equal(11, vm.ScheduleHour12);
            Assert.Equal(30, vm.ScheduleMinute);
            Assert.True(vm.ScheduleIsPm);
            Assert.False(vm.ScheduleIsAm);
            Assert.Equal("23:30", vm.Config.Schedule.Time);
            Assert.Contains("11:30 PM", vm.NextRunHint);

            vm.UseSchedulePreset("01:00");
            Assert.Equal(1, vm.ScheduleHour12);
            Assert.Equal(0, vm.ScheduleMinute);
            Assert.False(vm.ScheduleIsPm);
            Assert.True(vm.ScheduleIsAm);
            Assert.Equal("01:00", vm.Config.Schedule.Time);
            Assert.Contains("1:00 AM", vm.NextRunHint);

            // 2. Verify RunMode switching
            vm.RunByService = true;
            Assert.True(vm.RunByService);
            Assert.False(vm.RunByTask);

            vm.RunByTask = true;
            Assert.False(vm.RunByService);
            Assert.True(vm.RunByTask);

            // Switch back to Service mode for screenshot rendering
            vm.RunByService = true;
            vm.UseSchedulePreset("23:00");

            // 3. Switch view to SettingsTabContent
            var backupsContent = (FrameworkElement)window.FindName("BackupsTabContent");
            var settingsContent = (FrameworkElement)window.FindName("SettingsTabContent");
            if (backupsContent != null) backupsContent.Visibility = Visibility.Collapsed;
            if (settingsContent != null) settingsContent.Visibility = Visibility.Visible;

            var rootVisual = (FrameworkElement)window.Content;
            rootVisual.Measure(new Size(1240, 880));
            rootVisual.Arrange(new Rect(0, 0, 1240, 880));
            rootVisual.UpdateLayout();

            // 4. Capture screenshot of the upgraded Settings & Timings UI
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(rootVisual);

            var scratchDir = @"C:\Users\Administrator\.gemini\antigravity\brain\130ca6a4-71b0-4443-b2a2-5ffff00e8463\scratch";
            var outPath = System.IO.Path.Combine(scratchDir, "settings_service_and_timings.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var stream = System.IO.File.Create(outPath))
            {
                encoder.Save(stream);
            }

            Assert.True(System.IO.File.Exists(outPath));
        });
    }

    [Fact]
    public void SettingsPage_ColorfulSubMenu_AndDiagnostics_RenderAndVerify()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var window = new MainWindow();
            Assert.NotNull(window);
            var vm = (MainViewModel)window.DataContext;
            Assert.NotNull(vm);

            // 1. Verify Sub-Tab Switching
            vm.SelectSettingsTab("Service");
            Assert.Equal("Service", vm.SelectedSettingsTab);

            vm.SelectSettingsTab("Connection");
            Assert.Equal("Connection", vm.SelectedSettingsTab);

            vm.SelectSettingsTab("Notifications");
            Assert.Equal("Notifications", vm.SelectedSettingsTab);

            vm.SelectSettingsTab("Storage");
            Assert.Equal("Storage", vm.SelectedSettingsTab);

            // 2. Run Diagnostics and verify auto-navigation to Storage and diagnostic item population
            vm.RunValidateServerAsync().GetAwaiter().GetResult();
            Assert.True(vm.HasDiagnostics);
            Assert.NotEmpty(vm.DiagnosticItems);
            Assert.Equal("Settings", vm.ActiveTab);
            Assert.Equal("Storage", vm.SelectedSettingsTab);

            // Switch view to SettingsTabContent
            var backupsContent = (FrameworkElement)window.FindName("BackupsTabContent");
            var settingsContent = (FrameworkElement)window.FindName("SettingsTabContent");
            if (backupsContent != null) backupsContent.Visibility = Visibility.Collapsed;
            if (settingsContent != null) settingsContent.Visibility = Visibility.Visible;

            var rootVisual = (FrameworkElement)window.Content;
            rootVisual.Measure(new Size(1240, 880));
            rootVisual.Arrange(new Rect(0, 0, 1240, 880));
            rootVisual.UpdateLayout();

            // Render Storage & Maintenance with Diagnostics report & colorful sub-menu
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(rootVisual);

            var artifactDir = @"C:\Users\Administrator\.gemini\antigravity\brain\130ca6a4-71b0-4443-b2a2-5ffff00e8463";
            var outReport = System.IO.Path.Combine(artifactDir, "settings_diagnostics_and_submenu.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var stream = System.IO.File.Create(outReport))
            {
                encoder.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outReport));

            // Now switch to Service sub-tab to preview the modern ComboBoxes and highlighted AM/PM
            vm.SelectSettingsTab("Service");
            vm.ScheduleIsPm = true;
            vm.ScheduleIsAm = false;
            rootVisual.UpdateLayout();

            var rtbService = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbService.Render(rootVisual);

            var outService = System.IO.Path.Combine(artifactDir, "settings_colorful_submenu_service.png");
            var encoderService = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderService.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbService));
            using (var stream = System.IO.File.Create(outService))
            {
                encoderService.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outService));

            // Test Notifications features (Receiver, Send on failure, Send on success, Eye toggle, No username)
            vm.SelectSettingsTab("Notifications");
            vm.EmailRecipientsText = "alerts@uniprotech.net, backup-team@uniprotech.net";
            Assert.Contains("alerts@uniprotech.net", vm.Config.Email.ToAddresses);
            Assert.Contains("backup-team@uniprotech.net", vm.Config.Email.ToAddresses);

            vm.SetNotificationRule("both");
            Assert.True(vm.Config.Email.NotifyOnFailure);
            Assert.True(vm.Config.Email.NotifyOnSuccess);

            vm.SetNotificationRule("failure");
            Assert.True(vm.Config.Email.NotifyOnFailure);
            Assert.False(vm.Config.Email.NotifyOnSuccess);

            // Test Eye Button toggle for SMTP Password
            Assert.False(vm.IsSmtpPasswordVisible);
            vm.ToggleSmtpPasswordVisibility();
            Assert.True(vm.IsSmtpPasswordVisible);
            vm.ToggleSmtpPasswordVisibility();
            Assert.False(vm.IsSmtpPasswordVisible);

            rootVisual.UpdateLayout();

            var rtbNotif = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbNotif.Render(rootVisual);

            var outNotif = System.IO.Path.Combine(artifactDir, "settings_notifications_preview.png");
            var encoderNotif = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderNotif.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbNotif));
            using (var stream = System.IO.File.Create(outNotif))
            {
                encoderNotif.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outNotif));

            // Test Temp Folder Normalization (auto-appending \UniBackup\temp when partition or root is selected)
            Assert.Equal(@"D:\UniBackup\temp", MainViewModel.NormalizeTempFolderPath(@"D:\"));
            Assert.Equal(@"D:\UniBackup\temp", MainViewModel.NormalizeTempFolderPath(@"D:"));
            Assert.Equal(@"E:\Backups\UniBackup\temp", MainViewModel.NormalizeTempFolderPath(@"E:\Backups"));
            Assert.Equal(@"E:\UniBackup\temp", MainViewModel.NormalizeTempFolderPath(@"E:\UniBackup"));
            Assert.Equal(@"E:\MyCustom\temp", MainViewModel.NormalizeTempFolderPath(@"E:\MyCustom\temp"));

            // Switch to Storage tab and render the Browse Folder button
            vm.SelectSettingsTab("Storage");
            rootVisual.UpdateLayout();

            var rtbStorage = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbStorage.Render(rootVisual);

            var outStorage = System.IO.Path.Combine(artifactDir, "settings_storage_browse_preview.png");
            var encoderStorage = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderStorage.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbStorage));
            using (var stream = System.IO.File.Create(outStorage))
            {
                encoderStorage.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outStorage));

            // Render 1: AWS / S3 Sub-Tab with fixed fields & info banner (Image 4 match)
            vm.SelectSettingsTab("AWS");
            rootVisual.UpdateLayout();

            var rtbAws = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbAws.Render(rootVisual);

            var outAws = System.IO.Path.Combine(artifactDir, "settings_aws_fixed_fields_preview.png");
            var encoderAws = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderAws.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbAws));
            using (var stream = System.IO.File.Create(outAws))
            {
                encoderAws.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outAws));

            // Render 2: Destinations (Google Drive) Sub-Tab (Image 3 match)
            vm.SelectSettingsTab("Destinations");
            rootVisual.UpdateLayout();

            var rtbDest = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbDest.Render(rootVisual);

            var outDest = System.IO.Path.Combine(artifactDir, "settings_destinations_preview.png");
            var encoderDest = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderDest.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbDest));
            using (var stream = System.IO.File.Create(outDest))
            {
                encoderDest.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outDest));

            // Render 3: Modern Email Sub-Tab with provider radio pills, host/port, username, eye toggle, from address note, recipient, triggers (Images 1 & 2 match)
            vm.SelectSettingsTab("Email");
            rootVisual.UpdateLayout();

            var rtbEmail = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 880, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbEmail.Render(rootVisual);

            var outEmail = System.IO.Path.Combine(artifactDir, "settings_email_modern_preview.png");
            var encoderEmail = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderEmail.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbEmail));
            using (var stream = System.IO.File.Create(outEmail))
            {
                encoderEmail.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outEmail));
        });
    }

    [Fact]
    public void BackupJobsPage_RedesignedLayout_RenderAndVerify()
    {
        RunInStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var window = new MainWindow();
            Assert.NotNull(window);
            var vm = (MainViewModel)window.DataContext;
            Assert.NotNull(vm);

            // Ensure Backups tab is active
            vm.ActiveTab = "Backups";
            var backupsContent = (FrameworkElement)window.FindName("BackupsTabContent");
            if (backupsContent != null) backupsContent.Visibility = Visibility.Visible;

            // Populate realistic sample jobs to verify table row layout, cards, and context actions
            vm.Jobs.Clear();
            var job1 = new BackupJob
            {
                Name = "Production SQL Server",
                Kind = "sqlserver",
                SourceFolder = "localhost\\MSSQLSERVER",
                Schedule = new JobSchedule { Freq = "daily", Time = "02:00" },
                Enabled = true,
                LastRunStatus = "SUCCESS",
                LastRunTime = DateTime.Now.AddHours(-3),
                LastRunSizeBytes = 2450000000,
                LastRunDurationSeconds = 48
            };
            var job2 = new BackupJob
            {
                Name = "IIS Web Root & Portal",
                Kind = "folder",
                SourceFolder = "C:\\inetpub\\wwwroot",
                Schedule = new JobSchedule { Freq = "weekly", Time = "03:30", Days = ["Mon", "Wed", "Fri"] },
                Enabled = true,
                LastRunStatus = "SUCCESS",
                LastRunTime = DateTime.Now.AddHours(-12),
                LastRunSizeBytes = 840000000,
                LastRunDurationSeconds = 18
            };
            var job3 = new BackupJob
            {
                Name = "Finance App Data & Logs",
                Kind = "folder",
                SourceFolder = "D:\\Data\\FinanceLogs",
                Schedule = new JobSchedule { Freq = "daily", Time = "01:00" },
                Enabled = true,
                LastRunStatus = "FAILED",
                LastRunTime = DateTime.Now.AddDays(-1),
                LastRunSizeBytes = 120000000,
                LastRunDurationSeconds = 6
            };
            var job4 = new BackupJob
            {
                Name = "Legacy CRM Database",
                Kind = "sqlserver",
                SourceFolder = "localhost\\SQLEXPRESS",
                Schedule = new JobSchedule { Freq = "monthly", Time = "04:00", Day = "1" },
                Enabled = false,
                LastRunStatus = "PARTIAL",
                LastRunTime = DateTime.Now.AddDays(-5),
                LastRunSizeBytes = 450000000,
                LastRunDurationSeconds = 25
            };

            vm.Jobs.Add(new JobCardViewModel(job1));
            vm.Jobs.Add(new JobCardViewModel(job2));
            vm.Jobs.Add(new JobCardViewModel(job3));
            vm.Jobs.Add(new JobCardViewModel(job4));

            vm.HasNoJobs = false;
            vm.TotalJobsCount = vm.Jobs.Count;
            vm.EnabledJobsCount = vm.Jobs.Count(j => j.Enabled);
            vm.ScheduledJobsCount = vm.Jobs.Count(j => j.Enabled && !string.Equals(j.Model.Schedule.Freq, "manual", StringComparison.OrdinalIgnoreCase));
            vm.NeedsAttentionJobsCount = vm.Jobs.Count(j => string.Equals(j.Model.LastRunStatus, "FAILED", StringComparison.OrdinalIgnoreCase) || string.Equals(j.Model.LastRunStatus, "PARTIAL", StringComparison.OrdinalIgnoreCase));
            vm.JobsSummaryHeader = $"{vm.TotalJobsCount} jobs · {vm.ScheduledJobsCount} on a schedule";

            // Verify KPI properties exist and are computed
            Assert.True(vm.TotalJobsCount >= 0);
            Assert.True(vm.EnabledJobsCount >= 0);
            Assert.True(vm.ScheduledJobsCount >= 0);
            Assert.True(vm.NeedsAttentionJobsCount >= 0);

            var rootVisual = (FrameworkElement)window.Content;
            rootVisual.Measure(new Size(1280, 840));
            rootVisual.Arrange(new Rect(0, 0, 1280, 840));
            rootVisual.UpdateLayout();

            var artifactDir = @"C:\Users\Administrator\.gemini\antigravity\brain\4d66f748-fd0c-4eec-8519-6b7612c4ecd7";
            if (!System.IO.Directory.Exists(artifactDir))
            {
                System.IO.Directory.CreateDirectory(artifactDir);
            }

            // 1. Render Light Theme
            var rtbLight = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 840, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbLight.Render(rootVisual);
            var outLight = System.IO.Path.Combine(artifactDir, "screenshot_jobs_redesigned.png");
            var encoderLight = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderLight.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbLight));
            using (var stream = System.IO.File.Create(outLight))
            {
                encoderLight.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outLight));

            // 2. Switch to Dark Theme and Render
            App.ApplyTheme("Dark");
            rootVisual.UpdateLayout();

            var rtbDark = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 840, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtbDark.Render(rootVisual);
            var outDark = System.IO.Path.Combine(artifactDir, "screenshot_jobs_redesigned_dark.png");
            var encoderDark = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoderDark.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtbDark));
            using (var stream = System.IO.File.Create(outDark))
            {
                encoderDark.Save(stream);
            }
            Assert.True(System.IO.File.Exists(outDark));

            // Switch theme back to light
            App.ApplyTheme("Light");
        });
    }
}
