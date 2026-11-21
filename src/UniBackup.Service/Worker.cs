using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using Serilog;

namespace UniBackup.Service;

public class CloudPackerWorker : BackgroundService
{
    private readonly ConfigurationService _configService;
    private readonly BackupPipeline _pipeline;
    private readonly BatchBackupRunner _batchRunner;
    private readonly HashSet<string> _executedThisMinute = [];
    private int _lastEvaluatedMinute = -1;

    public CloudPackerWorker(
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
        Log.Information("UniBackup Windows Service started on {Machine}", Environment.MachineName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                // Reset executed cache once per minute
                if (now.Minute != _lastEvaluatedMinute)
                {
                    _executedThisMinute.Clear();
                    _lastEvaluatedMinute = now.Minute;
                }

                var config = await _configService.LoadConfigAsync();
                var jobs = await _configService.LoadJobsAsync();

                // 1. Check Nightly Batch Schedule
                if (IsScheduleDue(config.Schedule, now) && !_executedThisMinute.Contains("ALL_JOBS"))
                {
                    _executedThisMinute.Add("ALL_JOBS");
                    Log.Information("[Service] Triggering nightly batch backup for all enabled jobs at {Time}...", now);
                    await _batchRunner.RunAllAsync(config, null, stoppingToken);
                }

                // 2. Check Per-Job Schedules
                foreach (var job in jobs.Where(j => j.Enabled && !j.Schedule.Freq.Equals("manual", StringComparison.OrdinalIgnoreCase)))
                {
                    if (IsScheduleDue(job.Schedule, now) && !_executedThisMinute.Contains(job.Id))
                    {
                        _executedThisMinute.Add(job.Id);
                        Log.Information("[Service] Triggering scheduled backup for job '{JobName}' (ID: {JobId})...", job.Name, job.Id);
                        await _pipeline.ExecuteJobAsync(config, job, null, stoppingToken);
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

            // Poll every 30 seconds
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        Log.Information("UniBackup Windows Service is shutting down.");
    }

    private static bool IsScheduleDue(JobSchedule schedule, DateTime now)
    {
        if (schedule.Freq.Equals("manual", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TimeSpan.TryParse(schedule.Time, out var scheduledTime))
            return false;

        // Check if the current hour and minute match
        if (now.Hour != scheduledTime.Hours || now.Minute != scheduledTime.Minutes)
            return false;

        return schedule.Freq.ToLowerInvariant() switch
        {
            "daily" => true,
            "weekly" => schedule.Days.Any(d => d.Equals(now.ToString("ddd"), StringComparison.OrdinalIgnoreCase)),
            "monthly" => int.TryParse(schedule.Day, out var day) && now.Day == day,
            _ => false
        };
    }

    private static bool IsScheduleDue(ScheduleSettings schedule, DateTime now)
    {
        var jobSched = new JobSchedule
        {
            Freq = schedule.Freq,
            Time = schedule.Time,
            Days = schedule.Days,
            Day = schedule.Day
        };
        return IsScheduleDue(jobSched, now);
    }
}
