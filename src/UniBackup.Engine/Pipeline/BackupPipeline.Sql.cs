using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Security;
using Serilog;

namespace UniBackup.Engine.Pipeline;

public partial class BackupPipeline
{
    private readonly StreamedEncryptionService _encryption = new();
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
            var db = included[i];
            var curBasePct = 5 + i * slice;

            if (sql.DirectToUrl && ActiveProviders(config, job).All(p => p.Name == "s3") && string.IsNullOrEmpty(job.EncryptionPassphrase))
            {
                history.SqlDatabases.Add(await BackupOneDatabaseDirectToUrlAsync(
                    config, job, sql, db, requested, uploadedFiles, history, curBasePct, slice, report, ct));
            }
            else
            {
                history.SqlDatabases.Add(await BackupOneDatabaseAsync(
                    config, job, sql, db, requested, staging.Value, uploadedFiles, history, curBasePct, slice, report, ct));
            }
        }

        history.ArchiveName = $"{included.Count} database(s)";
        history.Status = SqlJobRules.OverallStatus(history.SqlDatabases, history.Destinations);
        if (history.Status != "SUCCESS")
        {
            history.ErrorMessage = string.Join("; ",
                history.SqlDatabases.Where(d => d.Status == "FAILED").Select(d => $"{d.Database}: {d.Message}")
                    .Concat(history.Destinations.Where(d => d.Status == "FAILED").Select(d => $"{d.Destination}: {d.ErrorMessage}")));
        }

        // Apply chain-aware retention across active cloud destinations
        foreach (var provider in ActiveProviders(config, job))
        {
            try
            {
                report("Retention", 95, $"Applying chain-aware retention on {provider.Name.ToUpperInvariant()}...");
                await ApplySqlRetentionAsync(config, job, provider, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SQL Retention] Retention cleanup on {Provider} encountered an issue: {Message}", provider.Name, ex.Message);
            }
        }
    }

    private async Task ApplySqlRetentionAsync(AppConfig config, BackupJob job, IStorageProvider provider, CancellationToken ct)
    {
        var retentionPolicy = job.Retention ?? new RetentionPolicy
        {
            KeepDays = job.RetentionDaysOverride ?? (provider.Name == "s3" ? config.Aws.RetentionDays : config.GDrive.RetentionDays)
        };

        if (retentionPolicy.KeepDays <= 0 && !retentionPolicy.GfsEnabled) return;

        var rootFolder = DestinationFolders.Resolve(provider.Name, config, job);
        var archives = await provider.ListArchivesAsync(config, rootFolder, ct);
        if (archives.IsFailure || archives.Value.Count == 0) return;

        var plan = SqlRetentionPlanner.Plan(archives.Value, retentionPolicy, DateTime.UtcNow);
        foreach (var deletion in plan.Deletions)
        {
            ct.ThrowIfCancellationRequested();
            Log.Information("[SQL Retention] Pruning expired backup {Key} (Reason: {Reason}) on {Provider}",
                deletion.Item.KeyOrId, deletion.Reason, provider.Name);
            await provider.DeleteAsync(config, deletion.Item.KeyOrId, ct);
        }
    }

    private async Task<SqlDatabaseRunResult> BackupOneDatabaseAsync(AppConfig config, BackupJob job, SqlServerSource sql,
        string db, SqlBackupType requested, string stagingFolder, IReadOnlySet<string> uploadedFiles, RunHistoryRecord history,
        double basePct, double slice, Action<string, double, string> report, CancellationToken ct)
    {
        var result = new SqlDatabaseRunResult { Database = db, BackupType = requested };
        string? file = null;
        string? encryptedFile = null;
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

            var fileToUpload = file;
            if (!string.IsNullOrWhiteSpace(job.EncryptionPassphrase))
            {
                report("Encrypting", basePct + slice * 0.65, $"{db}: AES-256-GCM streaming encryption");
                encryptedFile = file + ".ubenc";
                await _encryption.EncryptFileAsync(file, encryptedFile, job.EncryptionPassphrase, ct);
                fileToUpload = encryptedFile;
                result.FileName = Path.GetFileName(encryptedFile);
            }

            var uploadTasks = ActiveProviders(config, job, result.BackupType).Select(async provider =>
            {
                var folder = $"{DestinationFolders.Resolve(provider.Name, config, job)}/{SqlBackupFileName.RemoteSubfolder(db, result.BackupType)}";
                report("Uploading", basePct + slice * 0.7, $"{db}: uploading to {provider.Name.ToUpperInvariant()} in parallel");
                var upload = await UploadWithRetryAsync(provider, config, job, fileToUpload, folder, null, ct);
                return (Provider: provider.Name, Result: upload);
            }).ToList();

            var uploadResults = await Task.WhenAll(uploadTasks);
            var allUploaded = true;
            foreach (var (providerName, upload) in uploadResults)
            {
                allUploaded &= upload.IsSuccess;
                history.Destinations.Add(upload.IsSuccess
                    ? upload.Value
                    : new DestinationRunResult { Destination = providerName, Status = "FAILED", ErrorMessage = $"{db}: {upload.Error}" });
            }
            result.Uploaded = allUploaded;
            return result;
        }
        finally
        {
            if (encryptedFile != null && File.Exists(encryptedFile))
            {
                try { File.Delete(encryptedFile); }
                catch (Exception ex) { Log.Warning(ex, "Could not delete encrypted staged backup {File}", encryptedFile); }
            }
            if (file != null && File.Exists(file))
            {
                try { File.Delete(file); }
                catch (Exception ex) { Log.Warning(ex, "Could not delete staged backup {File}", file); }
            }
        }
    }

    private async Task<SqlDatabaseRunResult> BackupOneDatabaseDirectToUrlAsync(
        AppConfig config, BackupJob job, SqlServerSource sql, string db, SqlBackupType requested,
        IReadOnlySet<string> uploadedFiles, RunHistoryRecord history, double basePct, double slice,
        Action<string, double, string> report, CancellationToken ct)
    {
        var result = new SqlDatabaseRunResult { Database = db, BackupType = requested };
        try
        {
            var chain = await _sql.GetChainStateAsync(sql, db, ct);
            if (chain.IsFailure) return Failed(result, chain.Error!);

            result.BackupType = SqlJobRules.EffectiveType(requested, chain.Value, uploadedFiles);
            if (result.BackupType != requested)
                Log.Warning("[SQL Direct] {Db}: no {Requested} base, taking FULL instead", db, requested);

            var stamp = DateTime.Now;
            while (uploadedFiles.Contains(SqlBackupFileName.Format(db, result.BackupType, stamp))) stamp = stamp.AddSeconds(1);
            result.FileName = SqlBackupFileName.Format(db, result.BackupType, stamp);

            var s3Url = $"s3://{config.Aws.BucketName}/{DestinationFolders.Resolve("s3", config, job)}/{SqlBackupFileName.RemoteSubfolder(db, result.BackupType)}/{result.FileName}";
            report("Direct Backup", basePct + slice * 0.3, $"{db}: Direct-to-URL backup to S3");

            var credRes = await _sql.EnsureS3CredentialAsync(sql, $"s3://{config.Aws.BucketName}", config.Aws.AccessKeyId, config.Aws.SecretAccessKey, ct);
            if (credRes.IsFailure) return Failed(result, "S3 Credential creation failed: " + credRes.Error);

            var backupProgress = new Progress<int>(p =>
                report("Backing up", basePct + slice * 0.5 * p / 100, $"{db}: {result.BackupType} direct backup {p}%"));
            var backup = await _sql.BackupToUrlAsync(sql, db, result.BackupType, [s3Url], backupProgress, ct);
            if (backup.IsFailure) return Failed(result, backup.Error!);

            if (sql.Verify)
            {
                report("Verifying", basePct + slice * 0.8, $"{db}: RESTORE VERIFYONLY FROM URL");
                var verify = await _sql.VerifyFromUrlAsync(sql, [s3Url], ct);
                if (verify.IsFailure) return Failed(result, "Verify from URL failed: " + verify.Error);
            }

            result.Uploaded = true;
            history.Destinations.Add(new DestinationRunResult
            {
                Destination = "s3",
                Status = "SUCCESS",
                TargetLocation = s3Url
            });

            return result;
        }
        catch (Exception ex)
        {
            return Failed(result, "Direct-to-URL backup exception: " + ex.Message);
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
