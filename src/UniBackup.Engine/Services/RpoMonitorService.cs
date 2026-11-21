using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Engine.Services;

public record DatabaseRpoStatus(
    string Database,
    string JobId,
    DateTime? LastBackupTime,
    double ElapsedMinutes,
    int TargetRpoMinutes,
    bool IsBreached);

public class RpoMonitorService
{
    private readonly ConfigurationService _configService;
    private readonly INotificationService _notifier;
    private readonly HashSet<string> _activelyBreachedDbs = [];

    public RpoMonitorService(ConfigurationService configService, INotificationService notifier)
    {
        _configService = configService;
        _notifier = notifier;
    }

    public async Task<List<DatabaseRpoStatus>> CheckRpoComplianceAsync(
        AppConfig config,
        List<BackupJob> jobs,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var results = new List<DatabaseRpoStatus>();
        if (!config.Rpo.Enabled) return results;

        var runs = await _configService.HistoryRepository.GetRunsAsync(500);

        foreach (var job in jobs.Where(j => j.Enabled && j.IsSqlServer && j.Sql != null))
        {
            var databases = job.Sql!.Databases.Count > 0 ? job.Sql.Databases : ["master"];
            var targetRpo = job.RpoMinutes ?? config.Rpo.DefaultRpoMinutes;

            foreach (var db in databases)
            {
                if (config.Rpo.DatabaseRpoMinutes.TryGetValue(db, out var specificRpo))
                {
                    targetRpo = specificRpo;
                }

                // Find newest successful run for this db
                var newestRun = runs
                    .Where(r => r.JobId == job.Id && r.Status != "FAILED" &&
                                r.SqlDatabases.Any(sd => sd.Database.Equals(db, StringComparison.OrdinalIgnoreCase) && sd.Status == "SUCCESS"))
                    .OrderByDescending(r => r.Timestamp)
                    .FirstOrDefault();

                var lastBackup = newestRun?.Timestamp;
                var elapsed = lastBackup.HasValue ? (now - lastBackup.Value).TotalMinutes : double.MaxValue;
                var breached = elapsed > targetRpo;

                results.Add(new DatabaseRpoStatus(db, job.Id, lastBackup, elapsed, targetRpo, breached));

                if (breached && _activelyBreachedDbs.Add(db))
                {
                    Log.Warning("[RPO Breach] Database '{Db}' (Job '{Job}') has breached RPO target! Target: {Target}m, Elapsed: {Elapsed:F0}m",
                        db, job.Name, targetRpo, elapsed);
                }
                else if (!breached && _activelyBreachedDbs.Remove(db))
                {
                    Log.Information("[RPO Recovered] Database '{Db}' is now compliant with RPO target ({Target}m). Elapsed: {Elapsed:F0}m",
                        db, targetRpo, elapsed);
                }
            }
        }

        return results;
    }
}
