using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniBackup.UI.ViewModels;

public partial class NewBackupViewModel : ObservableObject
{
    private readonly ConfigurationService _configService;
    private readonly IIisDiscoveryService _iisService;
    private readonly ISchedulerService _schedulerService;

    [ObservableProperty]
    private string _jobName = "";

    [ObservableProperty]
    private string _sourceFolder = "";

    [ObservableProperty]
    private string _subfolder = "";

    [ObservableProperty]
    private bool _enableS3 = true;

    [ObservableProperty]
    private bool _enableGDrive = false;

    [ObservableProperty]
    private string _cloudFolder = "";

    [ObservableProperty]
    private bool _useVss = true;

    [ObservableProperty]
    private bool _includeInRunAll = true;

    [ObservableProperty]
    private string _selectedFrequency = "daily";

    [ObservableProperty]
    private string _scheduledTime = "02:00";

    [ObservableProperty]
    private bool _monday = true;

    [ObservableProperty]
    private bool _tuesday = true;

    [ObservableProperty]
    private bool _wednesday = true;

    [ObservableProperty]
    private bool _thursday = true;

    [ObservableProperty]
    private bool _friday = true;

    [ObservableProperty]
    private bool _saturday = false;

    [ObservableProperty]
    private bool _sunday = false;

    [ObservableProperty]
    private ObservableCollection<IisSiteInfo> _iisSites = [];

    [ObservableProperty]
    private IisSiteInfo? _selectedIisSite;

    [ObservableProperty]
    private bool _isIisLoaded;

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; private set; }

    public NewBackupViewModel(
        ConfigurationService configService,
        IIisDiscoveryService iisService,
        ISchedulerService schedulerService)
    {
        _configService = configService;
        _iisService = iisService;
        _schedulerService = schedulerService;
    }

    [RelayCommand]
    public void BrowseSourceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Source Folder to Backup"
        };

        if (dialog.ShowDialog() == true)
        {
            SourceFolder = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(JobName))
            {
                JobName = Path.GetFileName(dialog.FolderName);
            }
        }
    }

    [RelayCommand]
    public async Task LoadIisSitesAsync()
    {
        var result = await _iisService.DiscoverSitesAsync();
        IisSites.Clear();

        if (result.IsSuccess)
        {
            foreach (var site in result.Value)
            {
                IisSites.Add(site);
            }
            IsIisLoaded = true;
        }
        else
        {
            MessageBox.Show(result.Error, "IIS Discovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    partial void OnSelectedIisSiteChanged(IisSiteInfo? value)
    {
        if (value != null)
        {
            JobName = value.Name;
            SourceFolder = value.PhysicalPath;
        }
    }

    [RelayCommand]
    public async Task SaveJobAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder))
        {
            MessageBox.Show("Please select a valid source folder.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(SourceFolder))
        {
            var res = MessageBox.Show($"Source folder does not exist on disk:\n{SourceFolder}\n\nDo you still want to save this job?",
                "Missing Folder", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
        }

        var destinations = new List<string>();
        if (EnableS3) destinations.Add("s3");
        if (EnableGDrive) destinations.Add("gdrive");

        if (destinations.Count == 0)
        {
            MessageBox.Show("Please enable at least one destination (S3 or Google Drive).", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var days = new List<string>();
        if (Monday) days.Add("MON");
        if (Tuesday) days.Add("TUE");
        if (Wednesday) days.Add("WED");
        if (Thursday) days.Add("THU");
        if (Friday) days.Add("FRI");
        if (Saturday) days.Add("SAT");
        if (Sunday) days.Add("SUN");

        var job = new BackupJob
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(JobName) ? Path.GetFileName(SourceFolder) : JobName,
            SourceFolder = SourceFolder,
            Subfolder = string.IsNullOrWhiteSpace(Subfolder) ? null : Subfolder,
            Destinations = destinations,
            CloudFolder = string.IsNullOrWhiteSpace(CloudFolder) ? null : CloudFolder,
            UseVss = UseVss,
            IncludeInRunAll = IncludeInRunAll,
            Enabled = true,
            Schedule = new JobSchedule
            {
                Freq = SelectedFrequency,
                Time = ScheduledTime,
                Days = days
            }
        };

        var jobs = await _configService.LoadJobsAsync();
        jobs.Add(job);
        await _configService.SaveJobsAsync(jobs);
        await _schedulerService.SyncJobTaskAsync(job);

        DialogResult = true;
        CloseAction?.Invoke();
    }
}
