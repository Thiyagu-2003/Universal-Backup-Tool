using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Engine.Pipeline;

public partial class BackupPipeline
{
    private async Task RunSqlJobAsync(AppConfig config, BackupJob job, SqlBackupType requested,
        RunHistoryRecord history, Action<string, double, string> report, CancellationToken ct)
    {
        var sql = job.Sql;
        if (sql is null) { Fail(history, "SQL Server job has no 'sql' settings."); return; }

        report("Preflight", 5, $"Connecting to SQL Server {sql.Server}...");
        var databases = await _sql.ListDatabasesAsync(sql, ct);
        if (databases.IsFailure) { Fail(history, databases.Error!); return; }

        var (included, skipped) = SqlDatabaseSelector.Select(databases.Value, sql, requested);
        history.SqlDatabases.AddRange(skipped);
        if (included.Count == 0) { Fail(history, $"No databases matched this job's selection for a {requested} backup."); return; }

        if (ActiveProviders(config, job).Count == 0) { Fail(history, "No valid cloud destinations enabled for this job."); return; }

        var staging = await _sql.ResolveStagingFolderAsync(sql, job.Id, ct);
        if (staging.IsFailure) { Fail(history, staging.Error!); return; }
        SweepLeftovers(staging.Value); // the folder is private to this job (and the job lock is held); leftovers are from a crashed run

        // ponytail: scans history.json (capped at 1000 runs); Phase 2 moves this into SQLite run state.
        var uploadedFiles = (await _configService.LoadHistoryAsync())
            .Where(h => h.JobId == job.Id)
            .SelectMany(h => h.SqlDatabases)
            .Where(d => d.Uploaded && d.FileName != null)
            .Select(d => d.FileName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var slice = 90.0 / included.Count;
        for (var i = 0; i < included.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            history.SqlDatabases.Add(await BackupOneDatabaseAsync(
                config, job, sql, included[i], requested, staging.Value, uploadedFiles, history, 5 + i * slice, slice, report, ct));
        }

        history.ArchiveName = $"{included.Count} database(s)";
        history.Status = SqlJobRules.OverallStatus(history.SqlDatabases, history.Destinations);
        if (history.Status != "SUCCESS")
        {
            history.ErrorMessage = string.Join("; ",
                history.SqlDatabases.Where(d => d.Status == "FAILED").Select(d => $"{d.Database}: {d.Message}")
                    .Concat(history.Destinations.Where(d => d.Status == "FAILED").Select(d => $"{d.Destination}: {d.ErrorMessage}")));
        }
        // ponytail: no retention for SQL jobs until Phase 3 (age-based delete would break restore chains).
        Log.Information("[SQL] Retention is not applied to SQL Server jobs yet (chain-aware retention ships in Phase 3).");
    }

    private async Task<SqlDatabaseRunResult> BackupOneDatabaseAsync(AppConfig config, BackupJob job, SqlServerSource sql,
        string db, SqlBackupType requested, string stagingFolder, IReadOnlySet<string> uploadedFiles, RunHistoryRecord history,
        double basePct, double slice, Action<string, double, string> report, CancellationToken ct)
    {
        var result = new SqlDatabaseRunResult { Database = db, BackupType = requested };
        string? file = null;
        try
        {
            var chain = await _sql.GetChainStateAsync(sql, db, ct);
            if (chain.IsFailure) return Failed(result, chain.Error!);

            result.BackupType = SqlJobRules.EffectiveType(requested, chain.Value, uploadedFiles);
            if (result.BackupType != requested)
                Log.Warning("[SQL] {Db}: no {Requested} base that this job uploaded to every destination (none yet, upload failed, " +
                            "or another tool backed it up), taking FULL instead", db, requested);

            // Names have 1-second resolution. Never reuse a name that is already in the cloud: it would overwrite that
            // object and make the chain check above mistake this file for the uploaded one.
            var stamp = DateTime.Now;
            while (uploadedFiles.Contains(SqlBackupFileName.Format(db, result.BackupType, stamp))) stamp = stamp.AddSeconds(1);
            result.FileName = SqlBackupFileName.Format(db, result.BackupType, stamp);
            file = Path.Combine(stagingFolder, result.FileName);

            var backupProgress = new Progress<int>(p =>
                report("Backing up", basePct + slice * 0.5 * p / 100, $"{db}: {result.BackupType} backup {p}%"));
            var backup = await _sql.BackupAsync(sql, db, result.BackupType, file, backupProgress, ct);
            if (backup.IsFailure) return Failed(result, backup.Error!);

            result.SizeBytes = new FileInfo(file).Length;
            history.ArchiveSizeBytes += result.SizeBytes;

            if (sql.Verify)
            {
                report("Verifying", basePct + slice * 0.55, $"{db}: RESTORE VERIFYONLY");
                var verify = await _sql.VerifyAsync(sql, file, ct);
                if (verify.IsFailure) return Failed(result, "Verify failed: " + verify.Error);
            }

            if (sql.RunCheckDb && result.BackupType == SqlBackupType.Full)
            {
                report("Checking", basePct + slice * 0.6, $"{db}: DBCC CHECKDB");
                var check = await _sql.CheckDbAsync(sql, db, ct);
                if (check.IsFailure) Failed(result, "DBCC CHECKDB reported problems: " + check.Error); // still upload: newest copy is kept
            }

            var allUploaded = true;
            foreach (var provider in ActiveProviders(config, job))
            {
                var folder = $"{DestinationFolders.Resolve(provider.Name, config, job)}/{SqlBackupFileName.RemoteSubfolder(db, result.BackupType)}";
                report("Uploading", basePct + slice * 0.7, $"{db}: uploading to {provider.Name.ToUpperInvariant()}");
                var upload = await UploadWithRetryAsync(provider, config, job, file, folder, null, ct);
                allUploaded &= upload.IsSuccess;
                history.Destinations.Add(upload.IsSuccess
                    ? upload.Value
                    : new DestinationRunResult { Destination = provider.Name, Status = "FAILED", ErrorMessage = $"{db}: {upload.Error}" });
            }
            result.Uploaded = allUploaded;
            return result;
        }
        finally
        {
            if (file != null && File.Exists(file))
            {
                try { File.Delete(file); }
                catch (Exception ex) { Log.Warning(ex, "Could not delete staged backup {File}", file); }
            }
        }
    }

    private static void SweepLeftovers(string folder)
    {
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            try { File.Delete(f); }
            catch (Exception ex) { Log.Warning(ex, "Could not delete leftover staged backup {File}", f); }
        }
    }

    private static SqlDatabaseRunResult Failed(SqlDatabaseRunResult result, string message)
    {
        result.Status = "FAILED";
        result.Message = message;
        return result;
    }

    private static void Fail(RunHistoryRecord history, string message)
    {
        Log.Error("[SQL] {Message}", message);
        history.Status = "FAILED";
        history.ErrorMessage = message;
    }
}
