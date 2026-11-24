using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniBackup.UI.Dialogs;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class AnalyticsView : UserControl
{
    private AnalyticsViewModel? ViewModel => DataContext as AnalyticsViewModel;

    public AnalyticsView()
    {
        InitializeComponent();
    }

    private void DatePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && ViewModel != null)
        {
            ViewModel.SelectPreset(tag);
        }
    }

    private void FilterStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag && ViewModel != null)
        {
            ViewModel.SetStatusFilter(tag);
        }
    }

    private void ViewDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ActivityRowVm row)
        {
            OpenDetailsDialog(row);
        }
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid dg && dg.SelectedItem is ActivityRowVm row)
        {
            OpenDetailsDialog(row);
        }
    }

    private void OpenDetailsDialog(ActivityRowVm row)
    {
        var parentWindow = Window.GetWindow(this);
        var dlg = new ExecutionDetailsDialog(row, jobName => ViewModel?.NavigateToLogs(row))
        {
            Owner = parentWindow
        };
        dlg.ShowDialog();
    }
}
