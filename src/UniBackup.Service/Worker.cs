using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using Serilog;

namespace UniBackup.Service;

public class UniBackupWorker : BackgroundService
{
    private readonly ConfigurationService _configService;
    private readonly BackupPipeline _pipeline;
    private readonly BatchBackupRunner _batchRunner;
    private DateTime _lastMaintenanceDate = DateTime.MinValue;
    private DateTimeOffset? _lastNightlyBatchTime;

    public UniBackupWorker(
        ConfigurationService configService,
        BackupPipeline pipeline,
        BatchBackupRunner batchRunner)
    {
        _configService = configService;
        _pipeline = pipeline;
        _batchRunner = batchRunner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("UniBackup Windows Service started on {Machine} (Scheduler v2 & SQLite History active)", Environment.MachineName);

        // Ensure SQLite database & tables are initialized on startup
        try
        {
            await _configService.HistoryRepository.InitializeAsync();
            var legacyJson = Path.Combine(_configService.DataDirectory, "history.json");
            if (File.Exists(legacyJson))
            {
                var count = await _configService.HistoryRepository.ImportHistoryJsonAsync(legacyJson);
                if (count > 0)
                {
                    Log.Information("[Service] Imported {Count} history records from history.json into SQLite", count);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Service] Failed to initialize SQLite history repository on startup");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var config = await _configService.LoadConfigAsync();
                var jobs = await _configService.LoadJobsAsync();

                // 1. Daily Maintenance Tick (History pruning)
                if (now.Date != _lastMaintenanceDate.Date && now.Hour >= 3)
                {
                    _lastMaintenanceDate = now.Date;
                    try
                    {
                        var retention = config.Backup.HistoryRetentionDays > 0 ? config.Backup.HistoryRetentionDays : 90;
                        var pruned = await _configService.HistoryRepository.PruneHistoryAsync(retention);
                        if (pruned > 0)
                        {
                            Log.Information("[Service] Maintenance: Pruned {Count} history runs older than {Days} days", pruned, retention);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Service] Maintenance: History pruning failed");
                    }
                }

                // 2. Check Nightly Batch Schedule
                if (IsNightlyBatchDue(config.Schedule, now, _lastNightlyBatchTime))
                {
                    _lastNightlyBatchTime = now;
                    Log.Information("[Service] Triggering nightly batch backup for all enabled jobs at {Time}...", now);
                    await _batchRunner.RunAllAsync(config, null, stoppingToken);
                }

                // 3. Check Per-Job Schedules via ScheduleCalculator (Scheduler v2)
                foreach (var job in jobs.Where(j => j.Enabled && !j.Schedule.Freq.Equals("manual", StringComparison.OrdinalIgnoreCase)))
                {
                    var state = await _configService.GetRunStateAsync(job.Id) ?? new JobRunState { JobId = job.Id };

                    if (job.IsSqlServer)
                    {
                        var dueType = ScheduleCalculator.Due(job.Schedule, state, now, isSqlServer: true);
                        if (dueType.HasValue)
                        {
                            Log.Information("[Service] Scheduler v2: Triggering {Type} backup for SQL job '{JobName}' (ID: {JobId})...", dueType.Value, job.Name, job.Id);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _pipeline.ExecuteJobAsync(config, job, progress: null, ct: stoppingToken, sqlType: dueType.Value);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "[Service] Background SQL job '{JobName}' execution failed", job.Name);
                                }
                            }, stoppingToken);
                        }
                    }
                    else
                    {
                        if (ScheduleCalculator.IsFolderJobDue(job.Schedule, state, now))
                        {
                            Log.Information("[Service] Scheduler v2: Triggering folder backup for job '{JobName}' (ID: {JobId})...", job.Name, job.Id);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _pipeline.ExecuteJobAsync(config, job, null, stoppingToken);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "[Service] Background folder job '{JobName}' execution failed", job.Name);
                                }
                            }, stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Service] Error during background schedule evaluation: {Error}", ex.Message);
            }

            // Ticking every 15 seconds for precise sub-minute schedule detection
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }

        Log.Information("UniBackup Windows Service is shutting down.");
    }

    private static bool IsNightlyBatchDue(ScheduleSettings schedule, DateTimeOffset now, DateTimeOffset? lastBatchTime)
    {
        var jobSched = new JobSchedule
        {
            Freq = schedule.Freq,
            Time = schedule.Time,
            Days = schedule.Days,
            Day = schedule.Day
        };
        var dummyState = new JobRunState { LastRunTime = lastBatchTime };
        return ScheduleCalculator.IsFolderJobDue(jobSched, dummyState, now);
    }
}

// Backward-compatible alias for existing service registrations
public class CloudPackerWorker : UniBackupWorker
{
    public CloudPackerWorker(
        ConfigurationService configService,
        BackupPipeline pipeline,
        BatchBackupRunner batchRunner)
        : base(configService, pipeline, batchRunner)
    {
    }
}
