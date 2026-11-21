using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Core.Interfaces;

public interface ISchedulerService
{
    Task<Result> SyncJobTaskAsync(BackupJob job);
    Task<Result> RemoveJobTaskAsync(string jobId);
    Task<Result> SyncAllJobsTaskAsync(ScheduleSettings settings);
    Task<Result> RemoveAllJobsTaskAsync();
    Task<Result<string>> QueryTaskStatusAsync(string taskName);
    (bool Installed, bool Running, string State, string Detail) QueryAllJobsTaskInfo();
    Result RunAllJobsTaskNow();
    bool IsElevated();
}
