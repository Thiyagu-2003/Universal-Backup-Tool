using System.Configuration;
using System.Data;
using System.Windows;
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

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled WPF Dispatcher Exception: {Message}", e.Exception.Message);
        MessageBox.Show($"An unexpected UI error occurred:\n\n{e.Exception.Message}\n\nPlease check Application Logs for details.",
            "UniBackup Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

