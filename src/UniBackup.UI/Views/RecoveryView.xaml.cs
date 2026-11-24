using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryViewModel? ViewModel => DataContext as RecoveryViewModel;

    public RecoveryView()
    {
        InitializeComponent();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && ViewModel != null)
        {
            ViewModel.SelectedTab = tag;
        }
    }
}
