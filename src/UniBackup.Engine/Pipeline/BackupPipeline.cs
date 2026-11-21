using System.Diagnostics;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Logging;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Sql;
using Polly;
using Polly.Retry;
using Serilog;

namespace UniBackup.Engine.Pipeline;

public partial class BackupPipeline
{
    private readonly IArchiveService _archiveService;
    private readonly IEnumerable<IStorageProvider> _storageProviders;
    private readonly INotificationService _notificationService;
    private readonly WebhookNotificationService _webhookService;
    private readonly ConfigurationService _configService;
    private readonly SqlServerBackupService _sql;
    private readonly ScriptExecutionService _scriptService;

    public BackupPipeline(
        IArchiveService archiveService,
        IEnumerable<IStorageProvider> storageProviders,
        INotificationService notificationService,
        WebhookNotificationService webhookService,
        ConfigurationService configService,
        SqlServerBackupService sqlService)
    {
        _archiveService = archiveService;
        _storageProviders = storageProviders;
        _notificationService = notificationService;
        _webhookService = webhookService;
        _configService = configService;
        _sql = sqlService;
        _scriptService = new ScriptExecutionService(_sql);
    }

    public async Task<RunHistoryRecord> ExecuteJobAsync(
        AppConfig config,
        BackupJob job,
        IProgress<BackupProgressInfo>? progress = null,
        CancellationToken ct = default,
        SqlBackupType sqlType = SqlBackupType.Full)
    {
        // The service timer, Task Scheduler and the UI are separate processes; this lock stops them running one job twice at once.
        using var jobLock = TryLockJob(job.Id, out var alreadyRunning);
        if (alreadyRunning)
        {
            Log.Warning("[JOB SKIPPED] {JobName} (ID: {JobId}) is already running in another UniBackup process", job.Name, job.Id);
            return new RunHistoryRecord
            {
                RunId = Guid.NewGuid().ToString("N"),
                JobId = job.Id,
                JobName = job.Name,
                Timestamp = DateTime.UtcNow,
                Status = "FAILED",
                ErrorMessage = "This job is already running in another UniBackup process; this run was skipped."
            };
        }

        return await RunJobAsync(config, job, progress, ct, sqlType);
    }

    public string JobLockPath(string jobId) => Path.Combine(_configService.DataDirectory, "locks", $"{jobId}.lock");

    private FileStream? TryLockJob(string jobId, out bool alreadyRunning)
    {
        alreadyRunning = false;
        var path = JobLockPath(jobId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // FileShare.None = exclusive; the OS releases it if the process dies.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing / lock violation
        {
            alreadyRunning = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not create job lock {Path}; running without it", path);
            return null;
        }
    }

    private async Task<RunHistoryRecord> RunJobAsync(
        AppConfig config,
        BackupJob job,
        IProgress<BackupProgressInfo>? progress,
        CancellationToken ct,
        SqlBackupType sqlType)
    {
        var runId = Guid.NewGuid().ToString("N");
        var logFile = LoggingSetup.GetPerJobLogPath(job.Id);
        var sw = Stopwatch.StartNew();

        var history = new RunHistoryRecord
        {
            RunId = runId,
            JobId = job.Id,
            JobName = job.Name,
            Timestamp = DateTime.UtcNow,
            SourceFolder = job.EffectiveSourcePath,
            LogFile = logFile
        };

        void Report(string phase, double pct, string msg, string? file = null, long bytes = 0, long total = 0)
        {
            progress?.Report(new BackupProgressInfo
            {
                Phase = phase,
                Percent = pct,
                StatusMessage = msg,
                CurrentFile = file,
                BytesTransferred = bytes,
                TotalBytes = total
            });
        }

        string? stagedArchiveFile = null;

        try
        {
            // Pre-backup script
            if (job.PreScript != null && job.PreScript.Enabled)
            {
                Report("PreScript", 3, "Running pre-backup script...");
                var preRes = await _scriptService.ExecuteScriptAsync(job.PreScript, job, sqlType, ct);
                if (preRes.IsFailure && job.PreScript.AbortOnError)
                {
                    history.Status = "FAILED";
                    history.ErrorMessage = $"Pre-backup script failed: {preRes.Error}";
                    return history;
                }
            }

            if (job.IsSqlServer)
            {
                await RunSqlJobAsync(config, job, sqlType, history, (phase, pct, msg) => Report(phase, pct, msg), ct);
                return history; // finally{} finalises
            }

            Log.Information("========== [JOB START] {JobName} (ID: {JobId}) ==========", job.Name, job.Id);
            Report("Preflight", 5, "Performing pre-flight disk and path validation...");

            // 1. Validate source path
            if (!Directory.Exists(job.EffectiveSourcePath))
            {
                var err = $"Source directory does not exist or is unreachable: {job.EffectiveSourcePath}";
                Log.Error(err);
                history.Status = "FAILED";
                history.ErrorMessage = err;
                return history;
            }

            // 2. Prepare and validate Temp folder
            var tempFolder = config.Backup.TempFolder;
            if (!AppPaths.EnsureWritableDirectory(tempFolder, out tempFolder))
            {
                Log.Warning("Configured temp directory unwritable. Falling back to {Fallback}", tempFolder);
            }

            var freeSpace = AppPaths.GetFreeDiskSpaceBytes(tempFolder);
            if (freeSpace < 1024L * 1024L * 1024L) // < 1 GB warning
            {
                Log.Warning("Temp drive has low free space: {FreeSpace}", FormattingUtils.FormatBytes(freeSpace));
            }

            // 3. Compress to Zip archive
            var archiveFileName = job.GenerateArchiveName(DateTime.Now);
            stagedArchiveFile = Path.Combine(tempFolder, archiveFileName);
            history.ArchiveName = archiveFileName;

            Report("Compressing", 10, "Scanning and compressing files to zip archive...");

            var compressProgress = new Progress<(long processed, long total, string currentFile)>(t =>
            {
                var pct = t.total > 0 ? 10.0 + (t.processed / (double)t.total) * 40.0 : 10.0;
                Report("Compressing", pct, $"Compressing: {FormattingUtils.FormatBytes(t.processed)} / {FormattingUtils.FormatBytes(t.total)}", t.currentFile, t.processed, t.total);
            });

            var compressResult = await _archiveService.CreateZipArchiveAsync(
                job.EffectiveSourcePath,
                stagedArchiveFile,
                useVss: job.UseVss,
                compressProgress,
                ct);

            if (compressResult.IsFailure)
            {
                history.Status = "FAILED";
                history.ErrorMessage = compressResult.Error;
                return history;
            }

            history.ArchiveSizeBytes = compressResult.Value;
            Report("Uploading", 50, "Compression finished. Preparing cloud destinations...");

            // 4. Upload to destinations
            var activeProviders = ActiveProviders(config, job);

            if (activeProviders.Count == 0)
            {
                var err = "No valid cloud destinations enabled for this job.";
                Log.Error(err);
                history.Status = "FAILED";
                history.ErrorMessage = err;
                return history;
            }

            Report("Uploading", 60, "Uploading archive to all configured destinations in parallel...");

            var uploadTasks = activeProviders.Select(async provider =>
            {
                var uploadProgress = new Progress<(long uploaded, long total)>(u =>
                {
                    Report("Uploading", 75, $"Uploading to {provider.Name.ToUpperInvariant()}: {FormattingUtils.FormatBytes(u.uploaded)} / {FormattingUtils.FormatBytes(u.total)}", null, u.uploaded, u.total);
                });

                var uploadResult = await UploadWithRetryAsync(provider, config, job, stagedArchiveFile, null, uploadProgress, ct);

                if (uploadResult.IsSuccess)
                {
                    var retentionDays = job.RetentionDaysOverride ??
                                       (provider.Name == "s3" ? config.Aws.RetentionDays : config.GDrive.RetentionDays);

                    if (retentionDays > 0)
                    {
                        var folder = DestinationFolders.Resolve(provider.Name, config, job);
                        await provider.CleanupRetentionAsync(config, folder, retentionDays, ct);
                    }
                    return uploadResult.Value;
                }
                else
                {
                    return new DestinationRunResult
                    {
                        Destination = provider.Name,
                        Status = "FAILED",
                        ErrorMessage = uploadResult.Error
                    };
                }
            }).ToList();

            var destinationResults = await Task.WhenAll(uploadTasks);
            history.Destinations.AddRange(destinationResults);

            // Determine aggregate status
            var failedDests = history.Destinations.Count(d => d.Status == "FAILED");
            var successDests = history.Destinations.Count(d => d.Status == "SUCCESS");

            if (failedDests == 0 && successDests > 0)
                history.Status = "SUCCESS";
            else if (failedDests > 0 && successDests > 0)
                history.Status = "PARTIAL";
            else
                history.Status = "FAILED";

            Report("Finalizing", 95, "Finalizing backup run and cleaning staging files...");
        }
        catch (OperationCanceledException)
        {
            history.Status = "FAILED";
            history.ErrorMessage = "Backup cancelled by user.";
            Report("Finalizing", 100, "Backup cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error executing backup job {JobName}: {Error}", job.Name, ex.Message);
            history.Status = "FAILED";
            history.ErrorMessage = ex.Message;
        }
        finally
        {
            // 5. Clean up staged archive from temp folder
            if (!string.IsNullOrEmpty(stagedArchiveFile) && File.Exists(stagedArchiveFile))
            {
                try
                {
                    File.Delete(stagedArchiveFile);
                    Log.Information("Cleaned up staging temp file: {Path}", stagedArchiveFile);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not delete temporary staging file: {Path}", stagedArchiveFile);
                }
            }

            await FinalizeRunAsync(config, job, history, sw.Elapsed);
            Report("Finalizing", 100, $"Completed with status: {history.Status}");
        }

        return history;
    }

    private List<IStorageProvider> ActiveProviders(AppConfig config, BackupJob job, SqlBackupType? backupType = null)
    {
        var providers = _storageProviders
            .Where(p => job.Destinations.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.IsEnabled(config))
            .ToList();

        if (backupType.HasValue && job.DestinationRouting.Count > 0)
        {
            providers = providers.Where(p =>
            {
                var rule = job.DestinationRouting.FirstOrDefault(r => r.Destination.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                if (rule == null || rule.BackupTypes.Count == 0) return true;
                return rule.BackupTypes.Contains(backupType.Value);
            }).ToList();
        }

        return providers;
    }

    private static async Task<Result<DestinationRunResult>> UploadWithRetryAsync(
        IStorageProvider provider, AppConfig config, BackupJob job, string localFile, string? folder,
        IProgress<(long uploadedBytes, long totalBytes)>? progress, CancellationToken ct)
    {
        var retry = new ResiliencePipelineBuilder<Result<DestinationRunResult>>()
            .AddRetry(new RetryStrategyOptions<Result<DestinationRunResult>>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<Result<DestinationRunResult>>().HandleResult(r => r.IsFailure)
            })
            .Build();

        return await retry.ExecuteAsync(async token =>
        {
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var lastMove = DateTime.UtcNow;
            using var watchdogTimer = new System.Timers.Timer(10000);
            watchdogTimer.Elapsed += (_, _) =>
            {
                if ((DateTime.UtcNow - lastMove).TotalSeconds > 300)
                {
                    try { stallCts.Cancel(); } catch { }
                }
            };
            watchdogTimer.Start();

            try
            {
                var watchdogProgress = new Progress<(long u, long t)>(p =>
                {
                    lastMove = DateTime.UtcNow;
                    progress?.Report(p);
                });

                return await provider.UploadAsync(config, job, localFile, folder, watchdogProgress, stallCts.Token);
            }
            finally
            {
                watchdogTimer.Stop();
            }
        }, ct);
    }

    private async Task FinalizeRunAsync(AppConfig config, BackupJob job, RunHistoryRecord history, TimeSpan elapsed)
    {
        history.DurationSeconds = elapsed.TotalSeconds;

        // Update Job summary stats
        job.LastRunTime = DateTime.UtcNow;
        job.LastRunStatus = history.Status;
        job.LastRunDurationSeconds = history.DurationSeconds;
        job.LastRunSizeBytes = history.ArchiveSizeBytes;

        // Append to history.json
        await _configService.AppendHistoryAsync(history);

        // Update jobs.json
        var allJobs = await _configService.LoadJobsAsync();
        var existing = allJobs.FirstOrDefault(j => j.Id == job.Id);
        if (existing != null)
        {
            existing.LastRunTime = job.LastRunTime;
            existing.LastRunStatus = job.LastRunStatus;
            existing.LastRunDurationSeconds = job.LastRunDurationSeconds;
            existing.LastRunSizeBytes = job.LastRunSizeBytes;
            await _configService.SaveJobsAsync(allJobs);
        }

        var sqlType = history.SqlDatabases.FirstOrDefault()?.BackupType ?? SqlBackupType.Full;

        // Post-backup script
        if (job.PostScript != null && job.PostScript.Enabled)
        {
            await _scriptService.ExecuteScriptAsync(job.PostScript, job, sqlType, CancellationToken.None);
        }

        // Heartbeat ping
        var heartbeat = job.Heartbeat ?? (config.Heartbeat.Enabled ? config.Heartbeat : null);
        if (heartbeat != null && heartbeat.Enabled)
        {
            var hbService = new HeartbeatService();
            _ = hbService.PingAsync(heartbeat, history.Status == "SUCCESS", CancellationToken.None);
        }

        // Send notifications based on TriggerMatrix
        var matrix = job.TriggerMatrix ?? config.NotificationMatrix;
        if (matrix.ShouldNotify(sqlType, history.Status))
        {
            await _notificationService.SendRunReportAsync(config, job, history);
            await _webhookService.SendWebhookAlertAsync(config, job, history);
        }

        Log.Information("========== [JOB FINISHED] {JobName} -> Status: {Status} in {Duration} ==========",
            job.Name, history.Status, FormattingUtils.FormatDuration(elapsed));
    }
}
