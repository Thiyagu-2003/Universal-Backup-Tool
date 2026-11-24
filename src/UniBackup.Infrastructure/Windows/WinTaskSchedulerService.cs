using System.Security.Principal;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Microsoft.Win32.TaskScheduler;
using Serilog;
using Task = System.Threading.Tasks.Task;

namespace UniBackup.Infrastructure.Windows;

public class WinTaskSchedulerService : ISchedulerService
{
    private const string AllJobsTaskName = "UniBackup";

    public bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private string GetCliExecutablePath()
    {
        var baseDir = AppPaths.AppBaseDirectory;
        var directCli = Path.Combine(baseDir, "UniBackup.Cli.exe");
        if (File.Exists(directCli)) return directCli;

        var uiExe = Path.Combine(baseDir, "UniBackup.exe");
        if (File.Exists(uiExe)) return uiExe;

        return directCli;
    }

    public Task<Result> SyncJobTaskAsync(BackupJob job)
    {
        return Task.Run(() =>
        {
            try
            {
                using var ts = new TaskService();
                var taskName = job.TaskName;

                if (!job.Enabled || job.Schedule.Freq.Equals("manual", StringComparison.OrdinalIgnoreCase))
                {
                    ts.RootFolder.DeleteTask(taskName, false);
                    Log.Information("Removed scheduled task {TaskName} (job disabled or manual)", taskName);
                    return Result.Success();
                }

                var td = ts.NewTask();
                td.RegistrationInfo.Description = $"UniBackup automated backup task for job: {job.Name}";

                // Configure trigger
                ApplyScheduleTrigger(td, job.Schedule);

                // Configure action
                var exePath = GetCliExecutablePath();
                var args = $"--auto --job {job.Id}";
                td.Actions.Add(new ExecAction(exePath, args, Path.GetDirectoryName(exePath)));

                // Configure settings for server resilience
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.ExecutionTimeLimit = TimeSpan.FromHours(12);
                td.Settings.StartWhenAvailable = true;

                // Register task
                if (IsElevated())
                {
                    ts.RootFolder.RegisterTaskDefinition(
                        taskName,
                        td,
                        TaskCreation.CreateOrUpdate,
                        "SYSTEM",
                        null,
                        TaskLogonType.ServiceAccount);
                }
                else
                {
                    ts.RootFolder.RegisterTaskDefinition(
                        taskName,
                        td,
                        TaskCreation.CreateOrUpdate,
                        Environment.UserName,
                        null,
                        TaskLogonType.InteractiveToken);
                }

                Log.Information("Synchronized scheduled task: {TaskName} ({Schedule})", taskName, job.Schedule.ToDisplayString());
                return Result.Success();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to sync scheduled task {TaskName}: {Error}", job.TaskName, ex.Message);
                return Result.Failure($"Task Scheduler error: {ex.Message}");
            }
        });
    }

    public Task<Result> RemoveJobTaskAsync(string jobId)
    {
        return Task.Run(() =>
        {
            try
            {
                using var ts = new TaskService();
                var taskName = BackupJob.TaskNameFor(jobId);
                ts.RootFolder.DeleteTask(taskName, false);
                Log.Information("Deleted scheduled task: {TaskName}", taskName);
                return Result.Success();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to remove scheduled task for job {JobId}: {Error}", jobId, ex.Message);
                return Result.Failure($"Task removal error: {ex.Message}");
            }
        });
    }

    public Task<Result> SyncAllJobsTaskAsync(ScheduleSettings settings)
    {
        return Task.Run(() =>
        {
            try
            {
                using var ts = new TaskService();
                var td = ts.NewTask();
                td.RegistrationInfo.Description = "UniBackup nightly batch backup for all enabled jobs";

                var schedule = new JobSchedule
                {
                    Freq = settings.Freq,
                    Time = settings.Time,
                    Interval = settings.Interval,
                    Days = settings.Days,
                    Day = settings.Day,
                    Date = settings.Date
                };

                ApplyScheduleTrigger(td, schedule);

                var exePath = GetCliExecutablePath();
                var args = "--auto";
                td.Actions.Add(new ExecAction(exePath, args, Path.GetDirectoryName(exePath)));

                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.ExecutionTimeLimit = TimeSpan.FromHours(12);
                td.Settings.StartWhenAvailable = true;

                if (IsElevated())
                {
                    ts.RootFolder.RegisterTaskDefinition(
                        AllJobsTaskName,
                        td,
                        TaskCreation.CreateOrUpdate,
                        "SYSTEM",
                        null,
                        TaskLogonType.ServiceAccount);
                }
                else
                {
                    ts.RootFolder.RegisterTaskDefinition(
                        AllJobsTaskName,
                        td,
                        TaskCreation.CreateOrUpdate,
                        Environment.UserName,
                        null,
                        TaskLogonType.InteractiveToken);
                }

                Log.Information("Synchronized all-jobs scheduled task: {TaskName} ({Schedule})", AllJobsTaskName, schedule.ToDisplayString());
                return Result.Success();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to sync all-jobs scheduled task: {Error}", ex.Message);
                return Result.Failure($"Task Scheduler error: {ex.Message}");
            }
        });
    }

    public Task<Result> RemoveAllJobsTaskAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var ts = new TaskService();
                ts.RootFolder.DeleteTask(AllJobsTaskName, false);
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Failure($"Failed to remove all-jobs task: {ex.Message}");
            }
        });
    }

    public Task<Result<string>> QueryTaskStatusAsync(string taskName)
    {
        return Task.Run(() =>
        {
            try
            {
                using var ts = new TaskService();
                var task = ts.GetTask(taskName);
                if (task == null) return Result<string>.Failure("Task not found");

                var status = $"State: {task.State}, Next: {task.NextRunTime:yyyy-MM-dd HH:mm:ss}, Last: {task.LastRunTime:yyyy-MM-dd HH:mm:ss} (Result: 0x{task.LastTaskResult:X8})";
                return Result<string>.Success(status);
            }
            catch (Exception ex)
            {
                return Result<string>.Failure(ex.Message);
            }
        });
    }

    public (bool Installed, bool Running, string State, string Detail) QueryAllJobsTaskInfo()
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(AllJobsTaskName);
            if (task == null)
            {
                return (false, false, "NOT INSTALLED — no scheduled backups", "would run as SYSTEM · on-demand via Task Scheduler · no password needed");
            }
            var running = task.State == TaskState.Running;
            var user = task.Definition.Principal.UserId ?? Environment.UserName;
            var state = running ? "RUNNING" : "ACTIVE / READY";
            var detail = $"runs as {user} · daily at scheduled time" + (task.NextRunTime != DateTime.MinValue ? $" · next run: {task.NextRunTime:dd-MM-yyyy h:mm tt}" : "");
            return (true, running, state, detail);
        }
        catch (Exception ex)
        {
            return (false, false, "TASK SCHEDULER UNREACHABLE", ex.Message);
        }
    }

    public Result RunAllJobsTaskNow()
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(AllJobsTaskName);
            if (task == null)
            {
                return Result.Failure("Scheduled task is not installed. Click 'Install task' first.");
            }
            task.Run();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to run task: {ex.Message}");
        }
    }

    private static void ApplyScheduleTrigger(TaskDefinition td, JobSchedule s)
    {
        var (hour, minute) = ParseTime(s.Time);
        var startBoundary = DateTime.Today.AddHours(hour).AddMinutes(minute);
        if (startBoundary < DateTime.Now)
            startBoundary = startBoundary.AddDays(1);

        switch (s.Freq.ToLowerInvariant())
        {
            case "daily":
                td.Triggers.Add(new DailyTrigger((short)Math.Max(1, s.Interval))
                {
                    StartBoundary = startBoundary
                });
                break;

            case "weekly":
                var weeklyTrigger = new WeeklyTrigger
                {
                    StartBoundary = startBoundary,
                    WeeksInterval = (short)Math.Max(1, s.Interval),
                    DaysOfWeek = ParseDaysOfWeek(s.Days)
                };
                td.Triggers.Add(weeklyTrigger);
                break;

            case "monthly":
                int.TryParse(s.Day, out var dayNum);
                dayNum = Math.Clamp(dayNum, 1, 31);
                td.Triggers.Add(new MonthlyTrigger
                {
                    StartBoundary = startBoundary,
                    DaysOfMonth = [dayNum]
                });
                break;

            case "custom":
                var customTrigger = new TimeTrigger { StartBoundary = startBoundary };
                customTrigger.Repetition.Interval = TimeSpan.FromHours(Math.Max(1, s.Interval));
                td.Triggers.Add(customTrigger);
                break;

            case "onetime":
                DateTime.TryParse(s.Date, out var oneTimeDate);
                var triggerTime = (oneTimeDate == default ? DateTime.Today : oneTimeDate).AddHours(hour).AddMinutes(minute);
                td.Triggers.Add(new TimeTrigger { StartBoundary = triggerTime });
                break;

            case "logon":
                td.Triggers.Add(new LogonTrigger());
                break;

            case "startup":
                td.Triggers.Add(new BootTrigger());
                break;
        }
    }

    private static (int Hour, int Minute) ParseTime(string timeStr)
    {
        if (TimeSpan.TryParse(timeStr, out var ts))
            return (ts.Hours, ts.Minutes);
        return (2, 0); // 02:00 default
    }

    private static DaysOfTheWeek ParseDaysOfWeek(List<string> days)
    {
        var dow = (DaysOfTheWeek)0;
        foreach (var d in days)
        {
            dow |= d.ToUpperInvariant() switch
            {
                "MON" => DaysOfTheWeek.Monday,
                "TUE" => DaysOfTheWeek.Tuesday,
                "WED" => DaysOfTheWeek.Wednesday,
                "THU" => DaysOfTheWeek.Thursday,
                "FRI" => DaysOfTheWeek.Friday,
                "SAT" => DaysOfTheWeek.Saturday,
                "SUN" => DaysOfTheWeek.Sunday,
                _ => (DaysOfTheWeek)0
            };
        }
        return dow == 0 ? DaysOfTheWeek.AllDays : dow;
    }
}
