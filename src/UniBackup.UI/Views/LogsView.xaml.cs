using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LogsViewModel oldVm)
        {
            oldVm.ScrollRequested -= OnScrollRequested;
        }
        else if (e.OldValue is MainViewModel oldMain)
        {
            oldMain.Logs.ScrollRequested -= OnScrollRequested;
        }

        if (e.NewValue is LogsViewModel newVm)
        {
            newVm.ScrollRequested += OnScrollRequested;
        }
        else if (e.NewValue is MainViewModel newMain)
        {
            newMain.Logs.ScrollRequested += OnScrollRequested;
        }
    }

    private void OnScrollRequested(object? sender, LogLine line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (LogViewerList != null && line != null)
            {
                LogViewerList.ScrollIntoView(line);
            }
        });
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
