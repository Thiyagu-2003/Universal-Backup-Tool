using System.Diagnostics;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using Serilog;

namespace UniBackup.Engine.Pipeline;

public class BatchBackupRunner
{
    private readonly BackupPipeline _pipeline;
    private readonly ConfigurationService _configService;
    private readonly INotificationService _notificationService;

    public BatchBackupRunner(
        BackupPipeline pipeline,
        ConfigurationService configService,
        INotificationService notificationService)
    {
        _pipeline = pipeline;
        _configService = configService;
        _notificationService = notificationService;
    }

    public async Task<List<RunHistoryRecord>> RunAllAsync(
        AppConfig config,
        IProgress<(int current, int total, BackupProgressInfo info)>? overallProgress = null,
        CancellationToken ct = default)
    {
        var jobs = await _configService.LoadJobsAsync();
        var runnableJobs = jobs.Where(j => j.Enabled && j.IncludeInRunAll).ToList();
        var results = new List<RunHistoryRecord>();
        var totalSw = Stopwatch.StartNew();

        Log.Information("========== [BATCH RUN START] Running {Count} enabled jobs sequentially ==========", runnableJobs.Count);

        for (int i = 0; i < runnableJobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var job = runnableJobs[i];
            var currentIndex = i + 1;

            var jobProgress = new Progress<BackupProgressInfo>(info =>
            {
                overallProgress?.Report((currentIndex, runnableJobs.Count, info));
            });

            var history = await _pipeline.ExecuteJobAsync(config, job, jobProgress, ct);
            results.Add(history);
        }

        totalSw.Stop();
        Log.Information("========== [BATCH RUN COMPLETE] {Count} jobs finished in {Duration} ==========",
            runnableJobs.Count, FormattingUtils.FormatDuration(totalSw.Elapsed));

        // Send consolidated summary email
        await _notificationService.SendBatchReportAsync(config, results, totalSw.Elapsed, ct);

        return results;
    }
}
