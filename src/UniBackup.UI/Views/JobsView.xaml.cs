using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UniBackup.Core.Models;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class JobsView : UserControl
{
    public JobsView()
    {
        InitializeComponent();
    }

    private void JobRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var btn = FindParent<Button>(dep);
            var chk = FindParent<CheckBox>(dep);
            if (btn != null || chk != null) return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is JobCardViewModel vm && DataContext is MainViewModel mvm)
        {
            mvm.OpenJobDetails(vm);
        }
    }

    private void StatusFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string filter && DataContext is MainViewModel mvm)
        {
            mvm.SetJobsStatusFilter(filter);
        }
    }

    private void DatePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string preset && DataContext is MainViewModel mvm)
        {
            mvm.SelectJobsPreset(preset);
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.DataContext = btn.DataContext;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private JobCardViewModel? GetCardVm(object sender)
    {
        if (sender is MenuItem mi)
        {
            return mi.DataContext as JobCardViewModel
                ?? (mi.Parent as FrameworkElement)?.DataContext as JobCardViewModel
                ?? ((mi.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as JobCardViewModel;
        }
        return null;
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            mvm.OpenJobDetails(vm);
        }
    }

    private void RunFull_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            _ = mvm.RunJobWithTypeAsync(vm, SqlBackupType.Full);
        }
    }

    private void RunDiff_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            _ = mvm.RunJobWithTypeAsync(vm, SqlBackupType.Diff);
        }
    }

    private void RunLog_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            _ = mvm.RunJobWithTypeAsync(vm, SqlBackupType.Log);
        }
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            _ = mvm.ToggleJobCommand.ExecuteAsync(vm);
        }
    }

    private void EditJob_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            mvm.EditJob(vm);
        }
    }

    private void DuplicateJob_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null && DataContext is MainViewModel mvm)
        {
            _ = mvm.DuplicateJobCommand.ExecuteAsync(vm);
        }
    }

    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel mvm)
        {
            mvm.ActiveTab = "Summary";
        }
    }

    private void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetCardVm(sender);
        if (vm != null)
        {
            vm.IsDeletingInline = true;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        return parent as T;
    }
}
