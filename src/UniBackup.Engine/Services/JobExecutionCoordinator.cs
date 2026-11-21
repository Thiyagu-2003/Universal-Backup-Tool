using System.Collections.Concurrent;
using System.Threading.Channels;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;

namespace UniBackup.Engine.Services;

public class JobExecutionCoordinator
{
    private class ActiveRun
    {
        public string RunId { get; init; } = "";
        public string JobId { get; init; } = "";
        public string JobName { get; init; } = "";
        public DateTime StartTime { get; init; } = DateTime.UtcNow;
        public CancellationTokenSource Cts { get; init; } = new();
        public BackupProgressInfo? LastProgress { get; set; }
        public string Status { get; set; } = "RUNNING";
        public Task<RunHistoryRecord>? ExecutionTask { get; set; }
    }

    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new();
    private readonly Channel<ProgressEvent> _eventChannel = Channel.CreateUnbounded<ProgressEvent>(new UnboundedChannelOptions
    {
        SingleWriter = false,
        SingleReader = false
    });

    private readonly ConfigurationService _configService;
    private readonly BackupPipeline _pipeline;

    public JobExecutionCoordinator(ConfigurationService configService, BackupPipeline pipeline)
    {
        _configService = configService;
        _pipeline = pipeline;
    }

    public ChannelReader<ProgressEvent> EventReader => _eventChannel.Reader;

    public IReadOnlyList<ActiveRunInfo> GetActiveRuns()
    {
        return _activeRuns.Values
            .Select(r => new ActiveRunInfo(r.RunId, r.JobId, r.JobName, r.StartTime, r.Status, r.LastProgress))
            .ToList();
    }

    public async Task<Result<string>> TriggerRunAsync(
        string jobId,
        SqlBackupType? sqlType = null,
        CancellationToken callerCt = default)
    {
        var config = await _configService.LoadConfigAsync();
        var jobs = await _configService.LoadJobsAsync();
        var job = jobs.FirstOrDefault(j => j.Id.Equals(jobId, StringComparison.OrdinalIgnoreCase));

        if (job == null)
        {
            return Result<string>.Failure($"Job '{jobId}' not found.");
        }

        if (_activeRuns.Values.Any(r => r.JobId.Equals(jobId, StringComparison.OrdinalIgnoreCase) && r.Status == "RUNNING"))
        {
            return Result<string>.Failure($"Job '{job.Name}' ({jobId}) is already running.");
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);

        var activeRun = new ActiveRun
        {
            RunId = runId,
            JobId = job.Id,
            JobName = job.Name,
            StartTime = DateTime.UtcNow,
            Cts = linkedCts
        };

        var progress = new Progress<BackupProgressInfo>(p =>
        {
            activeRun.LastProgress = p;
            _ = _eventChannel.Writer.TryWrite(new ProgressEvent(runId, job.Id, DateTime.UtcNow, p));
        });

        _activeRuns[runId] = activeRun;

        activeRun.ExecutionTask = Task.Run(async () =>
        {
            try
            {
                var history = await _pipeline.ExecuteJobAsync(config, job, progress, linkedCts.Token, sqlType ?? SqlBackupType.Full);
                activeRun.Status = history.Status;
                return history;
            }
            catch (OperationCanceledException)
            {
                activeRun.Status = "CANCELLED";
                throw;
            }
            catch (Exception ex)
            {
                activeRun.Status = "FAILED";
                Log.Error(ex, "[Coordinator] Error executing job {JobName} ({RunId})", job.Name, runId);
                throw;
            }
            finally
            {
                _activeRuns.TryRemove(runId, out _);
                linkedCts.Dispose();
            }
        }, linkedCts.Token);

        return Result<string>.Success(runId);
    }

    public bool CancelRun(string runId)
    {
        if (_activeRuns.TryGetValue(runId, out var run))
        {
            run.Cts.Cancel();
            run.Status = "CANCELLING";
            Log.Information("[Coordinator] Cancel requested for run {RunId} ({JobName})", runId, run.JobName);
            return true;
        }
        return false;
    }

    public int CancelAll()
    {
        int count = 0;
        foreach (var run in _activeRuns.Values)
        {
            try
            {
                run.Cts.Cancel();
                run.Status = "CANCELLING";
                count++;
            }
            catch { }
        }
        Log.Information("[Coordinator] Cancel all requested ({Count} active runs)", count);
        return count;
    }

    public async Task WaitForShutdownAsync(TimeSpan timeout)
    {
        CancelAll();
        var tasks = _activeRuns.Values.Select(r => r.ExecutionTask).Where(t => t != null).ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks!), Task.Delay(timeout));
        }
    }
}
