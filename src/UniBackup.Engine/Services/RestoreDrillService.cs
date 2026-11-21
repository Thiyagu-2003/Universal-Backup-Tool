using System.Diagnostics;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Sql;

namespace UniBackup.Engine.Services;

public class RestoreDrillService
{
    private readonly SqlRestoreOrchestrator _restoreOrchestrator;
    private readonly SqlServerBackupService _sqlService;

    public RestoreDrillService(SqlRestoreOrchestrator restoreOrchestrator, SqlServerBackupService sqlService)
    {
        _restoreOrchestrator = restoreOrchestrator;
        _sqlService = sqlService;
    }

    public async Task<RestoreDrillResult> RunDrillAsync(
        AppConfig config,
        BackupJob job,
        string database,
        CancellationToken ct = default)
    {
        var drillDb = $"ub_drill_{FormattingUtils.SanitizePathSegment(database)}";
        var sw = Stopwatch.StartNew();

        try
        {
            Log.Information("[Restore Drill] Starting automated restore drill for {Db} -> {DrillDb}", database, drillDb);

            // 1. Restore latest chain into sandbox database
            var restoreRes = await _restoreOrchestrator.RestoreFromCloudAsync(
                config,
                job,
                database,
                targetTime: null,
                targetDatabaseName: drillDb,
                replace: true,
                report: null,
                ct: ct);

            if (restoreRes.IsFailure)
            {
                return new RestoreDrillResult(database, drillDb, false, sw.Elapsed.TotalSeconds, false, restoreRes.Error);
            }

            // 2. Run DBCC CHECKDB on the restored sandbox database
            var checkRes = await _sqlService.CheckDbAsync(job.Sql!, drillDb, ct);
            bool checkDbOk = checkRes.IsSuccess;

            sw.Stop();
            var rto = sw.Elapsed.TotalSeconds;

            Log.Information("[Restore Drill] Drill for {Db} finished in {Rto:F1}s with DBCC CHECKDB: {Status}",
                database, rto, checkDbOk ? "PASSED" : "FAILED (" + checkRes.Error + ")");

            return new RestoreDrillResult(database, drillDb, checkDbOk, rto, checkDbOk, checkDbOk ? null : checkRes.Error);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RestoreDrillResult(database, drillDb, false, sw.Elapsed.TotalSeconds, false, ex.Message);
        }
        finally
        {
            // 3. Drop sandbox database cleanly
            try
            {
                if (job.Sql != null)
                {
                    await _sqlService.DropDatabaseAsync(job.Sql, drillDb, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Restore Drill] Could not drop drill database {DrillDb}", drillDb);
            }
        }
    }
}
