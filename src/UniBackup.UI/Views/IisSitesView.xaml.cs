using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class IisSitesView : UserControl
{
    public IisSitesViewModel? ViewModel => DataContext as IisSitesViewModel;

    public IisSitesView()
    {
        InitializeComponent();
        HeaderSubline.Text = $"Inspect live web applications on {Environment.MachineName} and package for cloud backup";
        DataContextChanged += IisSitesView_DataContextChanged;
        Loaded += IisSitesView_Loaded;
    }

    private void IisSitesView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel mvm)
        {
            DataContext = mvm.IisSitesVm;
        }
    }

    private async void IisSitesView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel mvm)
        {
            DataContext = mvm.IisSitesVm;
        }
        else if (DataContext == null)
        {
            DataContext = new IisSitesViewModel();
        }

        if (ViewModel != null)
        {
            await ViewModel.InitializeAsync();
            SyncFilterRadio();
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string filter)
        {
            ViewModel?.SetFilter(filter);
        }
    }

    private void SyncFilterRadio()
    {
        if (ViewModel == null) return;
        var f = ViewModel.CurrentFilter;
        if (f == "All" && FilterAllRadio != null) FilterAllRadio.IsChecked = true;
        else if (f == "Running" && FilterRunningRadio != null) FilterRunningRadio.IsChecked = true;
        else if (f == "Stopped" && FilterStoppedRadio != null) FilterStoppedRadio.IsChecked = true;
        else if (f == "Unavailable" && FilterUnavailableRadio != null) FilterUnavailableRadio.IsChecked = true;
    }
}
