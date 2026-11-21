using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;

namespace UniBackup.Engine.Services;

public class SqlRestoreOrchestrator
{
    private readonly IEnumerable<IStorageProvider> _providers;
    private readonly SqlServerRestoreService _restoreService;
    private readonly StreamedEncryptionService _encryption = new();

    public SqlRestoreOrchestrator(IEnumerable<IStorageProvider> providers, SqlServerRestoreService restoreService)
    {
        _providers = providers;
        _restoreService = restoreService;
    }

    public async Task<Result> RestoreFromCloudAsync(
        AppConfig config,
        BackupJob job,
        string database,
        DateTime? targetTime = null,
        string? targetDatabaseName = null,
        string? providerName = null,
        string? passphraseOverride = null,
        bool replace = false,
        Action<string, double>? report = null,
        CancellationToken ct = default)
    {
        if (job.Sql == null)
        {
            return Result.Failure("Job is not configured as a SQL Server backup job.");
        }

        var provider = _providers.FirstOrDefault(p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase))
                       ?? _providers.FirstOrDefault(p => job.Destinations.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                       ?? _providers.FirstOrDefault();

        if (provider == null)
        {
            return Result.Failure("No suitable cloud storage provider found for restore.");
        }

        report?.Invoke($"Listing backups for database '{database}' from {provider.Name.ToUpperInvariant()}...", 5);
        var rootFolder = DestinationFolders.Resolve(provider.Name, config, job);
        var archivesRes = await provider.ListArchivesAsync(config, rootFolder, ct);
        if (archivesRes.IsFailure) return Result.Failure(archivesRes.Error!);

        report?.Invoke("Planning restore chain...", 15);
        var planRes = SqlRestorePlanner.Plan(archivesRes.Value, database, targetTime);
        if (planRes.IsFailure) return Result.Failure(planRes.Error!);

        var plan = planRes.Value!;
        var sequence = plan.Sequence;
        Log.Information("[SQL Restore] Resolved restore sequence for {Db}: {Count} files (Base: {Full}, Diff: {Diff}, Logs: {Logs})",
            database, sequence.Count, plan.FullBackup.DisplayName, plan.DiffBackup?.DisplayName ?? "none", plan.LogBackups.Count);

        var stagingDir = Path.Combine(config.Backup.TempFolder ?? Path.GetTempPath(), $"UniBackup_Restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        var readyFiles = new List<string>();
        try
        {
            var stepPct = 60.0 / sequence.Count;
            for (int i = 0; i < sequence.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = sequence[i];
                var curBase = 20.0 + (i * stepPct);

                report?.Invoke($"Downloading {item.DisplayName} ({i + 1}/{sequence.Count})...", curBase);
                var downloadPath = Path.Combine(stagingDir, item.DisplayName);
                var dl = await provider.DownloadAsync(config, item.KeyOrId, downloadPath, null, ct);
                if (dl.IsFailure) return Result.Failure($"Failed to download {item.DisplayName}: {dl.Error}");

                var finalPath = downloadPath;
                if (downloadPath.EndsWith(".ubenc", StringComparison.OrdinalIgnoreCase))
                {
                    report?.Invoke($"Decrypting {item.DisplayName}...", curBase + (stepPct * 0.5));
                    var pass = passphraseOverride ?? job.EncryptionPassphrase;
                    if (string.IsNullOrEmpty(pass))
                    {
                        return Result.Failure($"Backup {item.DisplayName} is encrypted with AES-256-GCM. Please provide --passphrase.");
                    }

                    var decryptedPath = downloadPath[..^6]; // strip .ubenc
                    await _encryption.DecryptFileAsync(downloadPath, decryptedPath, pass, ct);
                    File.Delete(downloadPath);
                    finalPath = decryptedPath;
                }

                readyFiles.Add(finalPath);
            }

            var destDb = string.IsNullOrWhiteSpace(targetDatabaseName) ? database : targetDatabaseName;
            report?.Invoke($"Restoring database '{destDb}' on SQL Server...", 85);

            string? targetDataPath = null;
            string? targetLogPath = null;
            if (!string.Equals(destDb, database, StringComparison.OrdinalIgnoreCase))
            {
                var paths = await _restoreService.GetInstanceDefaultPathsAsync(job.Sql, ct);
                if (paths.IsSuccess)
                {
                    targetDataPath = paths.Value.DataPath;
                    targetLogPath = paths.Value.LogPath;
                }
            }

            var restoreRes = await _restoreService.RestoreSequenceAsync(
                job.Sql,
                destDb,
                readyFiles,
                targetTime,
                replace,
                targetDataPath,
                targetLogPath,
                new Progress<int>(p => report?.Invoke($"Restoring database '{destDb}': {p}%", 85 + (p * 0.15))),
                ct);

            if (restoreRes.IsFailure) return restoreRes;

            report?.Invoke($"Database '{destDb}' successfully restored!", 100);
            Log.Information("[SQL Restore] Successfully restored database {Db} as {TargetDb}", database, destDb);
            return Result.Success();
        }
        finally
        {
            try { Directory.Delete(stagingDir, true); }
            catch (Exception ex) { Log.Warning(ex, "Could not delete restore staging directory {Dir}", stagingDir); }
        }
    }
}
