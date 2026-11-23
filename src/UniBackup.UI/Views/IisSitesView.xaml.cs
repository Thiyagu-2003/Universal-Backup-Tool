using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.Services;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class IisSitesView : UserControl
{
    private readonly IisCatalog _catalog = new();
    private List<IisSiteVm> _allSites = [];
    private readonly ObservableCollection<IisSiteVm> _filteredSites = [];
    private string _currentFilter = "All";

    public IisSitesView()
    {
        InitializeComponent();
        SitesItemsControl.ItemsSource = _filteredSites;
        HeaderSubline.Text = $"Inspect live web applications on {Environment.MachineName} and package for cloud backup";
        Loaded += IisSitesView_Loaded;
    }

    private async void IisSitesView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_allSites.Count == 0)
        {
            await LoadSitesAsync();
        }
    }

    private async Task LoadSitesAsync()
    {
        try
        {
            _allSites = await _catalog.DiscoverSitesAsync();
            ApplyFilter();
            _ = _catalog.ComputeSizesAsync(_allSites, null);
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Failed to read IIS sites: {ex.Message}", "IIS Discovery Failed");
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var sites = _allSites.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            sites = sites.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Pool.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (_currentFilter == "Running")
        {
            sites = sites.Where(s => s.Available && s.IsRunning);
        }
        else if (_currentFilter == "Stopped")
        {
            sites = sites.Where(s => s.Available && s.IsStopped);
        }
        else if (_currentFilter == "Unavailable")
        {
            sites = sites.Where(s => !s.Available);
        }

        _filteredSites.Clear();
        foreach (var s in sites)
        {
            _filteredSites.Add(s);
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string filter)
        {
            _currentFilter = filter;
            ApplyFilter();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadSitesAsync();
        NotificationHub.ShowSuccess($"Discovered {_allSites.Count} sites from IIS.", "IIS Refreshed");
    }

    private void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allSites.Where(s => s.Available && s.IsSelected).ToList();
        if (selected.Count == 0)
        {
            NotificationHub.ShowWarning("Select at least one available IIS site to back up.", "No Sites Selected");
            return;
        }

        if (DataContext is MainViewModel mvm)
        {
            mvm.OpenNewJob();
            if (mvm.NewJob != null)
            {
                mvm.NewJob.IsIisSite = true;
                mvm.NewJob.SelectedIisSites.Clear();
                foreach (var s in selected)
                {
                    mvm.NewJob.SelectedIisSites.Add(s);
                }
                mvm.NewJob.JobName = selected.Count == 1 ? selected[0].Name : $"{selected.Count} IIS sites";
                mvm.NewJob.SourceFolder = $"IIS:{string.Join(";", selected.Select(s => s.Name))}";
            }
            mvm.ActiveTab = "NewJob";
        }
    }
}
