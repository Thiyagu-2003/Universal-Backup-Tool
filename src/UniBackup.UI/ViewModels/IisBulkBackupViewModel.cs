using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;

namespace UniBackup.UI.ViewModels;

public partial class IisBulkBackupItemVm : ObservableObject
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeDisplay => SizeBytes > 0 ? FormattingUtils.FormatBytes(SizeBytes) : "0 B";
    public bool Available { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeSoftBrush))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private string _status = "Pending"; // Pending, Running, Succeeded, Skipped, Failed

    [ObservableProperty]
    private string _phase = "Queued";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    private string? _details;

    public bool HasErrorMessage => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasDetails => !string.IsNullOrEmpty(Details);
    public bool IsRunning => Status == "Running";
    public bool IsFinished => Status is "Succeeded" or "Skipped" or "Failed";

    public Brush StatusBrush => Status switch
    {
        "Succeeded" => (Brush)(Application.Current?.TryFindResource("BrushJade") ?? Brushes.Green),
        "Skipped" => (Brush)(Application.Current?.TryFindResource("BrushSaffron") ?? Brushes.Orange),
        "Failed" => (Brush)(Application.Current?.TryFindResource("BrushCoral") ?? Brushes.Red),
        "Running" => (Brush)(Application.Current?.TryFindResource("BrushCobalt") ?? Brushes.DodgerBlue),
        _ => (Brush)(Application.Current?.TryFindResource("BrushInkMuted") ?? Brushes.Gray)
    };

    public Brush StatusBadgeBrush => StatusBrush;

    public Brush StatusBadgeSoftBrush => Status switch
    {
        "Succeeded" => (Brush)(Application.Current?.TryFindResource("BrushJadeSoft") ?? Brushes.LightGreen),
        "Skipped" => (Brush)(Application.Current?.TryFindResource("BrushSaffronSoft") ?? Brushes.LightYellow),
        "Failed" => (Brush)(Application.Current?.TryFindResource("BrushCoralSoft") ?? Brushes.MistyRose),
        "Running" => (Brush)(Application.Current?.TryFindResource("BrushCobaltSoft") ?? Brushes.LightSkyBlue),
        _ => (Brush)(Application.Current?.TryFindResource("BrushSunken") ?? Brushes.LightGray)
    };
}

public partial class IisBulkBackupViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly BackupPipeline _pipeline;
    private readonly ConfigurationService _configService;
    private CancellationTokenSource? _cts;

    public ObservableCollection<IisBulkBackupItemVm> Items { get; } = [];

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _eligibleCount;

    [ObservableProperty]
    private int _ineligibleCount;

    [ObservableProperty]
    private long _totalSizeBytes;

    public string TotalSizeDisplay => FormattingUtils.FormatBytes(TotalSizeBytes);

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkippedSites))]
    private int _skippedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedSites))]
    private int _failedCount;

    public bool HasSkippedSites => SkippedCount > 0;
    public bool HasFailedSites => FailedCount > 0;
    public bool CanCancel => !IsRunning;

    [ObservableProperty]
    private int _processedCount;

    [ObservableProperty]
    private double _overallProgressPercent;

    [ObservableProperty]
    private string _currentOperationMessage = "Ready to start backup.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(IsConfirmationPhase))]
    [NotifyPropertyChangedFor(nameof(IsExecutionPhase))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(IsConfirmationPhase))]
    [NotifyPropertyChangedFor(nameof(IsCompletedPhase))]
    private bool _isCompleted;

    public bool CanStart => !IsRunning && !IsCompleted && EligibleCount > 0;
    public bool IsConfirmationPhase => !IsRunning && !IsCompleted;
    public bool IsExecutionPhase => IsRunning;
    public bool IsCompletedPhase => IsCompleted;

    public string ConfirmationSummaryText =>
        $"You have selected {TotalCount} {(TotalCount == 1 ? "website" : "websites")} ({TotalSizeDisplay} uncompressed) for backup.";

    public string IneligibleWarningText => IneligibleCount > 0
        ? $"{IneligibleCount} {(IneligibleCount == 1 ? "website is" : "websites are")} unavailable (path missing or inaccessible) and will be automatically skipped."
        : "";

    public bool HasIneligibleSites => IneligibleCount > 0;

    public Action? RequestClose { get; set; }

    public IisBulkBackupViewModel(
        AppConfig config,
        BackupPipeline pipeline,
        ConfigurationService configService,
        IEnumerable<IisSiteVm> sitesToBackup)
    {
        _config = config;
        _pipeline = pipeline;
        _configService = configService;

        foreach (var site in sitesToBackup)
        {
            var item = new IisBulkBackupItemVm
            {
                Name = site.Name,
                Path = site.Path,
                SizeBytes = site.SizeBytes,
                Available = site.Available
            };
            Items.Add(item);
        }

        TotalCount = Items.Count;
        EligibleCount = Items.Count(i => i.Available && !string.IsNullOrWhiteSpace(i.Path));
        IneligibleCount = TotalCount - EligibleCount;
        TotalSizeBytes = Items.Sum(i => i.SizeBytes);
    }

    [RelayCommand]
    public async Task StartBackupAsync()
    {
        if (IsRunning || IsCompleted) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SuccessCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        ProcessedCount = 0;
        OverallProgressPercent = 0;

        CurrentOperationMessage = "Validating site eligibility and initiating backup pipeline...";

        await Task.Run(async () =>
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                var item = Items[i];
                UpdateDispatcher(() =>
                {
                    ProcessedCount = i;
                    OverallProgressPercent = (double)i / Items.Count * 100.0;
                    CurrentOperationMessage = $"Processing site {i + 1} of {Items.Count}: {item.Name}";
                });

                // Eligibility check
                if (!item.Available || string.IsNullOrWhiteSpace(item.Path) || !Directory.Exists(item.Path))
                {
                    UpdateDispatcher(() =>
                    {
                        item.Status = "Skipped";
                        item.Phase = "Skipped";
                        item.ErrorMessage = !item.Available
                            ? "Site is marked unavailable (physical path or IIS resource missing)."
                            : $"Physical directory does not exist or is inaccessible: {item.Path}";
                        SkippedCount++;
                    });
                    continue;
                }

                UpdateDispatcher(() =>
                {
                    item.Status = "Running";
                    item.Phase = "Preflight";
                    item.Percent = 5;
                });

                var sw = Stopwatch.StartNew();
                try
                {
                    var existingJobs = await _configService.LoadJobsAsync();
                    var matchingJob = existingJobs.FirstOrDefault(j =>
                        string.Equals(j.Name, item.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(j.SourceFolder, item.Path, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(j.SourceFolder, $"IIS:{item.Name}", StringComparison.OrdinalIgnoreCase));

                    BackupJob job;
                    if (matchingJob != null)
                    {
                        job = matchingJob;
                        if (job.SourceFolder.StartsWith("IIS:", StringComparison.OrdinalIgnoreCase))
                        {
                            job.SourceFolder = item.Path;
                        }
                    }
                    else
                    {
                        var destinations = ResolveActiveDestinations(_config);
                        job = new BackupJob
                        {
                            Id = Guid.NewGuid().ToString("N")[..8],
                            Name = item.Name,
                            Kind = "folder",
                            SourceFolder = item.Path,
                            Destinations = destinations,
                            CloudFolder = string.IsNullOrWhiteSpace(_config.Aws.S3Prefix) ? "iis-sites" : _config.Aws.S3Prefix,
                            UseVss = true,
                            Enabled = true
                        };
                    }

                    var progress = new Progress<BackupProgressInfo>(p =>
                    {
                        UpdateDispatcher(() =>
                        {
                            item.Phase = p.Phase;
                            item.Percent = Math.Clamp(p.Percent, 5.0, 99.0);
                        });
                    });

                    var result = await _pipeline.ExecuteJobAsync(_config, job, progress, ct);
                    sw.Stop();

                    if (string.Equals(result.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateDispatcher(() =>
                        {
                            item.Status = "Succeeded";
                            item.Phase = "Completed";
                            item.Percent = 100;
                            item.Details = $"{FormattingUtils.FormatBytes(result.ArchiveSizeBytes)} archive created in {sw.Elapsed.TotalSeconds:N1}s";
                            SuccessCount++;
                        });
                    }
                    else
                    {
                        UpdateDispatcher(() =>
                        {
                            item.Status = "Failed";
                            item.Phase = "Failed";
                            item.ErrorMessage = result.ErrorMessage ?? "Pipeline executed with non-success status.";
                            FailedCount++;
                        });
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Log.Error(ex, "Bulk backup failed for site {SiteName}", item.Name);
                    UpdateDispatcher(() =>
                    {
                        item.Status = "Failed";
                        item.Phase = "Error";
                        item.ErrorMessage = ex.Message;
                        FailedCount++;
                    });
                }
            }

            UpdateDispatcher(() =>
            {
                ProcessedCount = Items.Count;
                OverallProgressPercent = 100;
                IsRunning = false;
                IsCompleted = true;
                CurrentOperationMessage =
                    $"Bulk backup completed: {SuccessCount} succeeded, {SkippedCount} skipped, {FailedCount} failed.";
            });
        }, ct);
    }

    [RelayCommand]
    public void CancelOrClose()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            IsRunning = false;
            IsCompleted = true;
            CurrentOperationMessage = "Bulk backup was cancelled.";
        }
        else
        {
            RequestClose?.Invoke();
        }
    }

    private static List<string> ResolveActiveDestinations(AppConfig config)
    {
        var dests = new List<string>();
        if (config.Local.Enabled) dests.Add("local");
        if (config.Aws.Enabled) dests.Add("s3");
        if (config.GDrive.Enabled) dests.Add("gdrive");
        if (config.Azure.Enabled) dests.Add("azure");
        if (config.Ftp.Enabled) dests.Add("ftp");
        if (config.Sftp.Enabled) dests.Add("sftp");
        if (config.WebDav.Enabled) dests.Add("webdav");
        if (config.Dropbox.Enabled) dests.Add("dropbox");
        if (config.OneDrive.Enabled) dests.Add("onedrive");
        if (config.Box.Enabled) dests.Add("box");

        if (dests.Count == 0)
        {
            // Default to local storage provider
            dests.Add("local");
        }
        return dests;
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
