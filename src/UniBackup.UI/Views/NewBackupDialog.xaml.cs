using System.Windows;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class NewBackupDialog : Window
{
    public NewBackupDialog(NewBackupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = Close;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
