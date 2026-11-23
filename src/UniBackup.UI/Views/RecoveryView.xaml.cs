using System.Windows;
using System.Windows.Controls;

namespace UniBackup.UI.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryView()
    {
        InitializeComponent();
    }

    private void RecoveryTab_Checked(object sender, RoutedEventArgs e)
    {
        if (SqlRecoveryPanel == null || FilesRecoveryPanel == null || DrillsRecoveryPanel == null) return;

        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            SqlRecoveryPanel.Visibility = tag == "SqlServer" ? Visibility.Visible : Visibility.Collapsed;
            FilesRecoveryPanel.Visibility = tag == "FilesAndSites" ? Visibility.Visible : Visibility.Collapsed;
            DrillsRecoveryPanel.Visibility = tag == "Drills" ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
