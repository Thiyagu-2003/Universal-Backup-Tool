using System.Windows;
using System.Windows.Input;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Dialogs;

public partial class IisBulkBackupDialog : Window
{
    public IisBulkBackupViewModel ViewModel { get; }

    public IisBulkBackupDialog(IisBulkBackupViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.RequestClose = () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                DialogResult = true;
                Close();
            });
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning) return;
        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !ViewModel.IsRunning)
        {
            DialogResult = true;
            Close();
        }
    }
}
