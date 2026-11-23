using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using UniBackup.Core.Common;
using UniBackup.UI.Services;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Dialogs;

public partial class IisPickerDialog : Window
{
    private readonly IisCatalog _catalog = new();
    private List<IisSiteVm> _allSites = [];
    private readonly ObservableCollection<IisSiteVm> _filteredSites = [];
    private string _currentFilter = "All";

    public List<IisSiteVm> SelectedSites => _allSites.Where(s => s.Available && s.IsSelected).ToList();
    public bool ExportConfiguration => ExportConfigCheckBox.IsChecked == true;

    public IisPickerDialog()
    {
        InitializeComponent();
        SitesItemsControl.ItemsSource = _filteredSites;
        SublineTextBlock.Text = $"Read live from IIS on {Environment.MachineName}. Sites whose folder is missing cannot be selected.";
        Loaded += IisPickerDialog_Loaded;
    }

    private async void IisPickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            var anim = new DoubleAnimation(0.96, 1.0, new Duration(TimeSpan.FromMilliseconds(260)))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };
            DialogScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
            DialogScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);
        }

        await LoadSitesAsync();
    }

    private async Task LoadSitesAsync()
    {
        try
        {
            _allSites = await _catalog.DiscoverSitesAsync();
            ApplyFilterAndSearch();
            UpdateSummary();

            // Background size calculations
            _ = _catalog.ComputeSizesAsync(_allSites, _ =>
            {
                UpdateSummary();
            });
        }
        catch (Exception ex)
        {
            NotificationHub.ShowError($"Failed to read IIS sites: {ex.Message}", "IIS Discovery Failed");
        }
    }

    private void ApplyFilterAndSearch()
    {
        var query = SearchInput.Text.Trim();
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

    private void UpdateSummary()
    {
        var available = _allSites.Count(s => s.Available);
        var selected = _allSites.Where(s => s.Available && s.IsSelected).ToList();
        var selectedCount = selected.Count;
        var selectedBytes = selected.Sum(s => s.SizeBytes);

        SelectedCountRun.Text = selectedCount.ToString();
        AvailableCountRun.Text = available.ToString();
        SelectedSizeRun.Text = FormattingUtils.FormatBytes(selectedBytes);

        SubmitButton.Content = selectedCount > 0 ? $"Add {selectedCount} sites" : "Add sites";
        SubmitButton.IsEnabled = selectedCount > 0;
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string filter)
        {
            _currentFilter = filter;
            ApplyFilterAndSearch();
        }
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilterAndSearch();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = SelectAllCheckBox.IsChecked == true;
        foreach (var site in _filteredSites.Where(s => s.Available))
        {
            site.IsSelected = isChecked;
        }
        UpdateSummary();
    }

    private void SiteCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadSitesAsync();
        NotificationHub.ShowSuccess($"Discovered {_allSites.Count} sites from IIS.", "IIS Refreshed");
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Enter && SubmitButton.IsEnabled)
        {
            DialogResult = true;
            Close();
        }
    }
}
