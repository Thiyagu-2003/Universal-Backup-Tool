using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }

    private void DatePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string preset && DataContext is MainViewModel vm)
        {
            vm.SelectOverviewPreset(preset);
        }
    }
}
