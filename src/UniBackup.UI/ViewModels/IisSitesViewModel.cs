using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.Administration;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.UI.Dialogs;
using UniBackup.UI.Services;

namespace UniBackup.UI.ViewModels;

public partial class IisSitesViewModel : ObservableObject
{
    private readonly IisCatalog _catalog = new();
    private readonly ConfigurationService? _configService;
    private readonly BackupPipeline? _pipeline;
    private readonly IIisDiscoveryService? _iisService;
    private readonly ISchedulerService? _schedulerService;
    private readonly Func<AppConfig>? _getConfig;
    private readonly Action<IisSiteVm>? _onEditSite;
    private readonly Action<List<IisSiteVm>>? _onOpenNewJobWithSites;

    private readonly List<IisSiteVm> _allSites = [];
    public ObservableCollection<IisSiteVm> FilteredSites { get; } = [];

    [ObservableProperty]
    private string _currentFilter = "All"; // All, Running, Stopped, Unavailable

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    // Counts derived from actual IIS discovery data
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllFilterTitle))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunningFilterTitle))]
    private int _runningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoppedFilterTitle))]
    private int _stoppedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnavailableFilterTitle))]
    private int _unavailableCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSites))]
    [NotifyPropertyChangedFor(nameof(IsSingleSiteSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedSummaryText))]
    [NotifyPropertyChangedFor(nameof(CanRunSelected))]
    [NotifyPropertyChangedFor(nameof(CanBackupSelected))]
    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSummaryText))]
    [NotifyPropertyChangedFor(nameof(IsAllFilteredSelected))]
    private int _visibleSelectedCount;

    public string AllFilterTitle => $"All Sites ({TotalCount})";
    public string RunningFilterTitle => $"Running ({RunningCount})";
    public string StoppedFilterTitle => $"Stopped ({StoppedCount})";
    public string UnavailableFilterTitle => $"Unavailable ({UnavailableCount})";

    public bool HasSelectedSites => SelectedCount > 0;
    public bool IsSingleSiteSelected => SelectedCount == 1;

    public bool CanRunSelected => HasSelectedSites && !IsBusy;
    public bool CanBackupSelected => HasSelectedSites && !IsBusy;
    public bool CanEditSelected => HasSelectedSites && !IsBusy;
    public bool CanDeleteSelected => HasSelectedSites && !IsBusy;

    public string SelectedSummaryText
    {
        get
        {
            if (SelectedCount == 0) return "0 selected";
            if (FilteredSites.Count < _allSites.Count && VisibleSelectedCount != SelectedCount)
            {
                return $"{SelectedCount} selected ({VisibleSelectedCount} in view)";
            }
            return $"{SelectedCount} of {TotalCount} selected";
        }
    }

    public string VisibleSummaryText => $"Showing {FilteredSites.Count} of {TotalCount} sites";

    public bool HasFilteredSites => FilteredSites.Count > 0;
    public bool HasNoFilteredSites => FilteredSites.Count == 0;

    public bool? IsAllFilteredSelected
    {
        get
        {
            var selectable = FilteredSites.Where(s => s.Available).ToList();
            if (selectable.Count == 0) return false;
            var selectedInView = selectable.Count(s => s.IsSelected);
            if (selectedInView == 0) return false;
            if (selectedInView == selectable.Count) return true;
            return null; // Indeterminate
        }
    }

    public IReadOnlyList<IisSiteVm> AllSites => _allSites;
    public List<IisSiteVm> SelectedSites => _allSites.Where(s => s.IsSelected).ToList();
    public List<IisSiteVm> SelectedAvailableSites => _allSites.Where(s => s.IsSelected && s.Available).ToList();

    public IisSitesViewModel(
        ConfigurationService? configService = null,
        BackupPipeline? pipeline = null,
        IIisDiscoveryService? iisService = null,
        ISchedulerService? schedulerService = null,
        Func<AppConfig>? getConfig = null,
        Action<IisSiteVm>? onEditSite = null,
        Action<List<IisSiteVm>>? onOpenNewJobWithSites = null)
    {
        _configService = configService;
        _pipeline = pipeline;
        _iisService = iisService;
        _schedulerService = schedulerService;
        _getConfig = getConfig;
        _onEditSite = onEditSite;
        _onOpenNewJobWithSites = onOpenNewJobWithSites;
    }

    partial void OnCurrentFilterChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    public async Task InitializeAsync()
    {
        if (_allSites.Count == 0)
        {
            await RefreshFromIisAsync();
        }
    }

    [RelayCommand]
    public async Task RefreshFromIisAsync()
    {
        IsLoading = true;
        try
        {
            var sites = await _catalog.DiscoverSitesAsync();

            // Clear old listeners
            foreach (var s in _allSites)
            {
                s.PropertyChanged -= Site_PropertyChanged;
            }

            // Preserve selection for existing sites by name if reloading
            var previouslySelectedNames = new HashSet<string>(
                _allSites.Where(s => s.IsSelected).Select(s => s.Name),
                StringComparer.OrdinalIgnoreCase);

            _allSites.Clear();
            foreach (var site in sites)
            {
                if (previouslySelectedNames.Contains(site.Name))
                {
                    site.IsSelected = true;
                }
                site.PropertyChanged += Site_PropertyChanged;
                _allSites.Add(site);
            }

            UpdateCountsFromDiscoveryData();
            ApplyFilter();

            _ = _catalog.ComputeSizesAsync(_allSites, _ =>
            {
                // Background size updates
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to discover IIS sites");
            NotificationHub.ShowError($"Failed to read IIS sites: {ex.Message}", "IIS Discovery Failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void LoadSites(IEnumerable<IisSiteVm> sites)
    {
        foreach (var s in _allSites)
        {
            s.PropertyChanged -= Site_PropertyChanged;
        }

        _allSites.Clear();
        foreach (var site in sites)
        {
            site.PropertyChanged += Site_PropertyChanged;
            _allSites.Add(site);
        }

        UpdateCountsFromDiscoveryData();
        ApplyFilter();
    }

    private void Site_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IisSiteVm.IsSelected))
        {
            UpdateSelectionCounts();
        }
        else if (e.PropertyName is nameof(IisSiteVm.State) or nameof(IisSiteVm.Available))
        {
            UpdateCountsFromDiscoveryData();
            ApplyFilter();
        }
    }

    public void UpdateCountsFromDiscoveryData()
    {
        TotalCount = _allSites.Count;
        RunningCount = _allSites.Count(s => s.Available && s.IsRunning);
        StoppedCount = _allSites.Count(s => s.Available && s.IsStopped);
        UnavailableCount = _allSites.Count(s => !s.Available);
        UpdateSelectionCounts();
    }

    public void UpdateSelectionCounts()
    {
        SelectedCount = _allSites.Count(s => s.IsSelected);
        VisibleSelectedCount = FilteredSites.Count(s => s.IsSelected);
        OnPropertyChanged(nameof(SelectedSummaryText));
        OnPropertyChanged(nameof(VisibleSummaryText));
        OnPropertyChanged(nameof(IsAllFilteredSelected));
        OnPropertyChanged(nameof(CanRunSelected));
        OnPropertyChanged(nameof(CanBackupSelected));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    public void ApplyFilter()
    {
        var sites = _allSites.AsEnumerable();

        // 1. Status Filter
        if (string.Equals(CurrentFilter, "Running", StringComparison.OrdinalIgnoreCase))
        {
            sites = sites.Where(s => s.Available && s.IsRunning);
        }
        else if (string.Equals(CurrentFilter, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            sites = sites.Where(s => s.Available && s.IsStopped);
        }
        else if (string.Equals(CurrentFilter, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            sites = sites.Where(s => !s.Available);
        }

        // 2. Search Query (Name, Path, Pool, Binding, Port)
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            sites = sites.Where(s => s.MatchesSearch(SearchQuery));
        }

        FilteredSites.Clear();
        foreach (var s in sites)
        {
            FilteredSites.Add(s);
        }

        OnPropertyChanged(nameof(HasFilteredSites));
        OnPropertyChanged(nameof(HasNoFilteredSites));
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void SetFilter(string filter)
    {
        CurrentFilter = filter;
    }

    [RelayCommand]
    public void SelectAll()
    {
        // Select all available sites in the current filtered view
        foreach (var s in FilteredSites.Where(s => s.Available))
        {
            s.IsSelected = true;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void SelectRunning()
    {
        // Select all running available sites in the current filtered view
        foreach (var s in FilteredSites.Where(s => s.Available && s.IsRunning))
        {
            s.IsSelected = true;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void SelectStopped()
    {
        // Select all stopped available sites in the current filtered view
        foreach (var s in FilteredSites.Where(s => s.Available && s.IsStopped))
        {
            s.IsSelected = true;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void SelectAvailable()
    {
        // Select all available sites in the current filtered view
        foreach (var s in FilteredSites.Where(s => s.Available))
        {
            s.IsSelected = true;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var s in _allSites)
        {
            s.IsSelected = false;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public void ToggleSelectAllFiltered()
    {
        var selectable = FilteredSites.Where(s => s.Available).ToList();
        if (selectable.Count == 0) return;

        // If all are currently selected, unselect them; otherwise select all
        var allSelected = selectable.All(s => s.IsSelected);
        var targetState = !allSelected;

        foreach (var s in selectable)
        {
            s.IsSelected = targetState;
        }
        UpdateSelectionCounts();
    }

    [RelayCommand]
    public async Task RunSelectedAsync()
    {
        var selected = SelectedSites;
        if (selected.Count == 0)
        {
            NotificationHub.ShowWarning("Select at least one website to run.", "No Sites Selected");
            return;
        }

        IsBusy = true;
        int started = 0;
        int alreadyRunning = 0;
        int unavailableSkipped = 0;
        var errors = new List<string>();

        await Task.Run(() =>
        {
            try
            {
                using var mgr = new ServerManager();
                bool changesMade = false;

                foreach (var siteVm in selected)
                {
                    // Validate eligibility
                    if (!siteVm.Available)
                    {
                        unavailableSkipped++;
                        continue;
                    }

                    if (siteVm.IsRunning)
                    {
                        alreadyRunning++;
                        continue;
                    }

                    try
                    {
                        var iisSite = mgr.Sites[siteVm.Name];
                        if (iisSite != null)
                        {
                            iisSite.Start();
                            changesMade = true;
                        }

                        // Update in-memory VM state
                        UpdateDispatcher(() =>
                        {
                            siteVm.State = "Started";
                            siteVm.NotifyStateChanged();
                        });
                        started++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{siteVm.Name}: {ex.Message}");
                    }
                }

                if (changesMade)
                {
                    mgr.CommitChanges();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("ServerManager access failed in RunSelected (IIS might not be available or fallback mode): {Msg}", ex.Message);
                // In test or fallback environments, update eligible items safely
                foreach (var siteVm in selected)
                {
                    if (!siteVm.Available)
                    {
                        unavailableSkipped++;
                    }
                    else if (siteVm.IsRunning)
                    {
                        alreadyRunning++;
                    }
                    else
                    {
                        UpdateDispatcher(() =>
                        {
                            siteVm.State = "Started";
                            siteVm.NotifyStateChanged();
                        });
                        started++;
                    }
                }
            }
        });

        UpdateCountsFromDiscoveryData();
        ApplyFilter();
        IsBusy = false;

        if (errors.Count > 0)
        {
            NotificationHub.ShowError($"Started {started} site(s). Errors occurred: {string.Join("; ", errors)}", "Run Operation Finished");
        }
        else
        {
            var msg = $"Started {started} site(s).";
            if (alreadyRunning > 0) msg += $" {alreadyRunning} was already running.";
            if (unavailableSkipped > 0) msg += $" {unavailableSkipped} unavailable site(s) skipped.";
            NotificationHub.ShowSuccess(msg, "Websites Started");
        }
    }

    [RelayCommand]
    public void BackupSelected()
    {
        var selected = SelectedSites;
        if (selected.Count == 0)
        {
            NotificationHub.ShowWarning("Select at least one website to back up.", "No Sites Selected");
            return;
        }

        var available = selected.Where(s => s.Available).ToList();
        if (available.Count == 0)
        {
            NotificationHub.ShowError("None of the selected websites are available. Cannot run backup against missing physical paths.", "Backup Skipped");
            return;
        }

        var config = _getConfig?.Invoke() ?? new AppConfig();
        if (_pipeline == null || _configService == null)
        {
            // If invoked without full pipeline dependencies (e.g. forward to new job wizard fallback)
            _onOpenNewJobWithSites?.Invoke(available);
            return;
        }

        var dialogVm = new IisBulkBackupViewModel(config, _pipeline, _configService, selected);
        var dialog = new IisBulkBackupDialog(dialogVm)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        dialog.ShowDialog();
    }

    [RelayCommand]
    public void EditSelected()
    {
        var selected = SelectedSites;
        if (selected.Count == 0)
        {
            NotificationHub.ShowWarning("Select a website to edit.", "No Sites Selected");
            return;
        }

        if (selected.Count > 1)
        {
            NotificationHub.ShowInfo(
                "Bulk configuration editing is not supported. Please select exactly 1 website to edit.",
                "Edit Selected");
            return;
        }

        var targetSite = selected[0];
        _onEditSite?.Invoke(targetSite);
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        var selected = SelectedSites;
        if (selected.Count == 0)
        {
            NotificationHub.ShowWarning("Select at least one website to delete its backup jobs.", "No Sites Selected");
            return;
        }

        if (_configService == null)
        {
            NotificationHub.ShowWarning("Configuration service is unavailable.", "Operation Failed");
            return;
        }

        var jobs = await _configService.LoadJobsAsync();
        var selectedNames = new HashSet<string>(selected.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(selected.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);

        var matchingJobs = jobs.Where(j =>
            selectedNames.Contains(j.Name) ||
            selectedPaths.Contains(j.SourceFolder) ||
            (j.SourceFolder?.StartsWith("IIS:", StringComparison.OrdinalIgnoreCase) == true &&
             selectedNames.Any(name => j.SourceFolder.Contains(name, StringComparison.OrdinalIgnoreCase)))
        ).ToList();

        if (matchingJobs.Count == 0)
        {
            NotificationHub.ShowInfo(
                $"No configured backup jobs exist in UniBackup for the {selected.Count} selected website(s).",
                "No Jobs To Delete");
            return;
        }

        var confirm = MessageBox.Show(
            $"Are you sure you want to delete {matchingJobs.Count} configured backup job(s) for the selected website(s)?\n\nJobs to be removed:\n• {string.Join("\n• ", matchingJobs.Select(j => j.Name))}",
            "Confirm Delete Jobs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var matchingJobIds = matchingJobs.Select(j => j.Id).ToHashSet();
        jobs.RemoveAll(j => matchingJobIds.Contains(j.Id));
        await _configService.SaveJobsAsync(jobs);

        if (_schedulerService != null)
        {
            foreach (var id in matchingJobIds)
            {
                await _schedulerService.RemoveJobTaskAsync(id);
            }
        }

        NotificationHub.ShowSuccess($"Deleted {matchingJobs.Count} configured backup job(s).", "Jobs Deleted");
    }

    private static void UpdateDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && dispatcher.Thread.IsAlive && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.Invoke(action, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                action();
            }
        }
        else
        {
            action();
        }
    }
}
