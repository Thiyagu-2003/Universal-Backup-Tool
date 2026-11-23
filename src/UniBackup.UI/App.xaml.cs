using System.Windows;
using Microsoft.Win32;
using Serilog;

namespace UniBackup.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyAnimationSettings();
    }

    public static void ApplyTheme(string theme)
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;

            var targetTheme = theme;
            if (string.Equals(theme, "Match Windows", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase))
            {
                targetTheme = IsWindowsInDarkMode() ? "Dark" : "Light";
            }

            var themeUri = string.Equals(targetTheme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? new Uri("pack://application:,,,/UniBackup;component/Themes/Dark.xaml", UriKind.Absolute)
                : new Uri("pack://application:,,,/UniBackup;component/Themes/Light.xaml", UriKind.Absolute);

            var dicts = app.Resources.MergedDictionaries;
            for (int i = 0; i < dicts.Count; i++)
            {
                var src = dicts[i].Source?.OriginalString ?? "";
                if (src.Contains("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    src.Contains("Dark.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    dicts[i] = new ResourceDictionary { Source = themeUri };
                    return;
                }
            }

            dicts.Add(new ResourceDictionary { Source = themeUri });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply UI theme {Theme}: {Message}", theme, ex.Message);
        }
    }

    public static void ApplyAnimationSettings()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;

            // Honor "Show animations in Windows" (SystemParameters.ClientAreaAnimation)
            if (!SystemParameters.ClientAreaAnimation)
            {
                app.Resources["DurationFast"] = new Duration(TimeSpan.Zero);
                app.Resources["DurationNormal"] = new Duration(TimeSpan.Zero);
                app.Resources["DurationWindowOpen"] = new Duration(TimeSpan.Zero);
                app.Resources["DurationEntrance"] = new Duration(TimeSpan.Zero);
                app.Resources["DurationLong"] = new Duration(TimeSpan.Zero);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to inspect animation parameters: {Message}", ex.Message);
        }
    }

    private static bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch { }
        return false;
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled WPF Dispatcher Exception: {Message}", e.Exception.Message);
        MessageBox.Show($"An unexpected UI error occurred:\n\n{e.Exception.Message}\n\nPlease check Application Logs for details.",
            "UniBackup Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
