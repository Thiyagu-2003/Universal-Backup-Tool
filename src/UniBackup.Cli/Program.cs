using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Logging;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Infrastructure.Storage;
using UniBackup.Infrastructure.Windows;
using Serilog;

namespace UniBackup.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Dependency and Service Wiring
        var crypto = new CredentialProtector();
        var configService = new ConfigurationService(crypto);
        var config = await configService.LoadConfigAsync();

        // 2. Initialize Logging
        LoggingSetup.Initialize(config.Backup.LogPath, config.Backup.MaxLogMb, config.Backup.KeepLogsDays);

        var archiveService = new HighSpeedZipArchiveService();
        var s3Storage = new AwsS3StorageProvider();
        var gdriveStorage = new GoogleDriveStorageProvider();
        var storageProviders = new IStorageProvider[] { s3Storage, gdriveStorage };

        var emailService = new EmailNotificationService();
        var webhookService = new WebhookNotificationService();
        var schedulerService = new WinTaskSchedulerService();
        var iisService = new IisSiteDiscoveryService();

        var pipeline = new BackupPipeline(archiveService, storageProviders, emailService, webhookService, configService, new SqlServerBackupService(crypto));
        var batchRunner = new BatchBackupRunner(pipeline, configService, emailService);
        var diagnostics = new ServerDiagnosticsService(storageProviders, emailService, iisService, configService);

        Console.WriteLine($"UniBackup .NET v2.0 - Host: {Environment.MachineName}");
        Console.WriteLine("=========================================================");

        // 3. Handle Arguments
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--service"))
        {
            var sIndex = Array.IndexOf(args, "--service");
            var subCommand = sIndex < args.Length - 1 ? args[sIndex + 1].ToLowerInvariant() : "status";

            switch (subCommand)
            {
                case "install":
                    var instRes = WindowsServiceManager.InstallService();
                    Console.WriteLine(instRes.IsSuccess ? "Windows Service 'UniBackup' installed successfully." : $"Error: {instRes.Error}");
                    return instRes.IsSuccess ? 0 : 1;

                case "uninstall":
                    var unRes = WindowsServiceManager.UninstallService();
                    Console.WriteLine(unRes.IsSuccess ? "Windows Service 'UniBackup' uninstalled successfully." : $"Error: {unRes.Error}");
                    return unRes.IsSuccess ? 0 : 1;

                case "start":
                    var startRes = WindowsServiceManager.StartService();
                    Console.WriteLine(startRes.IsSuccess ? "Windows Service 'UniBackup' started." : $"Error: {startRes.Error}");
                    return startRes.IsSuccess ? 0 : 1;

                case "stop":
                    var stopRes = WindowsServiceManager.StopService();
                    Console.WriteLine(stopRes.IsSuccess ? "Windows Service 'UniBackup' stopped." : $"Error: {stopRes.Error}");
                    return stopRes.IsSuccess ? 0 : 1;

                case "status":
                default:
                    var status = WindowsServiceManager.GetServiceStatus();
                    Console.WriteLine($"Windows Service 'UniBackup' Status: {status}");
                    return 0;
            }
        }

        if (args.Contains("--restore"))
        {
            var jobIndex = Array.IndexOf(args, "--job");
            var dbIndex = Array.IndexOf(args, "--db");
            var atIndex = Array.IndexOf(args, "--at");
            var asIndex = Array.IndexOf(args, "--as");
            var provIndex = Array.IndexOf(args, "--provider");
            var passIndex = Array.IndexOf(args, "--passphrase");
            var replace = args.Contains("--replace");

            if (jobIndex < 0 || jobIndex >= args.Length - 1 || dbIndex < 0 || dbIndex >= args.Length - 1)
            {
                Console.Error.WriteLine("Error: --restore requires --job <id> and --db <databaseName>.");
                Console.WriteLine("Usage: UniBackup.Cli.exe --restore --job <id> --db <name> [--at <yyyy-MM-dd HH:mm:ss>] [--as <newName>] [--provider <s3|gdrive>] [--passphrase <pwd>] [--replace]");
                return 1;
            }

            var jobId = args[jobIndex + 1];
            var dbName = args[dbIndex + 1];
            DateTime? targetTime = null;
            if (atIndex >= 0 && atIndex < args.Length - 1)
            {
                if (DateTime.TryParse(args[atIndex + 1], out var parsedTime))
                {
                    targetTime = parsedTime;
                }
                else
                {
                    Console.Error.WriteLine($"Error: Invalid date/time format for --at '{args[atIndex + 1]}'. Expected 'yyyy-MM-dd HH:mm:ss'.");
                    return 1;
                }
            }

            var newDbName = asIndex >= 0 && asIndex < args.Length - 1 ? args[asIndex + 1] : null;
            var providerName = provIndex >= 0 && provIndex < args.Length - 1 ? args[provIndex + 1] : null;
            var passphrase = passIndex >= 0 && passIndex < args.Length - 1 ? args[passIndex + 1] : null;

            var jobs = await configService.LoadJobsAsync();
            var job = jobs.FirstOrDefault(j => j.Id.Equals(jobId, StringComparison.OrdinalIgnoreCase));
            if (job == null)
            {
                Console.Error.WriteLine($"Error: Job '{jobId}' was not found in jobs.json");
                return 1;
            }

            Console.WriteLine($"Starting restore of database '{dbName}' from cloud...");
            if (targetTime.HasValue) Console.WriteLine($"Target Point-in-Time: {targetTime.Value:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrEmpty(newDbName)) Console.WriteLine($"Restoring As Database: '{newDbName}'");

            var restoreService = new SqlServerRestoreService(crypto);
            var orchestrator = new SqlRestoreOrchestrator(storageProviders, restoreService);

            var result = await orchestrator.RestoreFromCloudAsync(
                config,
                job,
                dbName,
                targetTime,
                newDbName,
                providerName,
                passphrase,
                replace,
                (status, pct) =>
                {
                    Console.Write($"\r[Restore] {pct:F0}% {status.PadRight(50)}");
                });

            Console.WriteLine();
            if (result.IsSuccess)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[SUCCESS] Restore of database '{newDbName ?? dbName}' completed successfully!");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[FAILED] Restore failed: {result.Error}");
                Console.ResetColor();
                return 1;
            }
        }

        if (args.Contains("--list-jobs"))
        {
            var jobs = await configService.LoadJobsAsync();
            Console.WriteLine($"\nConfigured Backup Jobs ({jobs.Count}):");
            Console.WriteLine("---------------------------------------------------------");
            foreach (var j in jobs)
            {
                var status = j.Enabled ? "ENABLED " : "DISABLED";
                Console.WriteLine($"[{j.Id}] {j.Name,-25} | {status} | {j.Schedule.ToDisplayString(),-20} | {j.EffectiveSourcePath}");
            }
            return 0;
        }

        if (args.Contains("--validate"))
        {
            Console.WriteLine("\nRunning Server Diagnostics...\n");
            var results = await diagnostics.RunDiagnosticsAsync(config);
            var anyFail = false;

            foreach (var item in results)
            {
                var tag = item.Status switch
                {
                    "PASS" => "[PASS]",
                    "WARN" => "[WARN]",
                    _ => "[FAIL]"
                };

                Console.ForegroundColor = item.Status switch
                {
                    "PASS" => ConsoleColor.Green,
                    "WARN" => ConsoleColor.Yellow,
                    _ => ConsoleColor.Red
                };

                Console.Write($"{tag,-8}");
                Console.ResetColor();
                Console.WriteLine($"{item.Category,-10} {item.Name,-28} : {item.Details}");

                if (item.Status == "FAIL") anyFail = true;
            }

            return anyFail ? 1 : 0;
        }

        if (args.Contains("--auto"))
        {
            var jobIndex = Array.IndexOf(args, "--job");
            if (jobIndex >= 0 && jobIndex < args.Length - 1)
            {
                var jobId = args[jobIndex + 1];
                var jobs = await configService.LoadJobsAsync();
                var job = jobs.FirstOrDefault(j => j.Id.Equals(jobId, StringComparison.OrdinalIgnoreCase));

                if (job == null)
                {
                    Log.Error("Job not found: {JobId}", jobId);
                    Console.Error.WriteLine($"Error: Job '{jobId}' was not found in jobs.json");
                    return 1;
                }

                Console.WriteLine($"Executing single job: {job.Name} (ID: {job.Id})");
                var progress = new Progress<BackupProgressInfo>(p =>
                {
                    Console.Write($"\r[{p.Phase,-12}] {p.Percent:F0}% {p.StatusMessage.PadRight(50)}");
                });

                var typeIndex = Array.IndexOf(args, "--type");
                var sqlType = typeIndex >= 0 && typeIndex < args.Length - 1
                              && Enum.TryParse<SqlBackupType>(args[typeIndex + 1], ignoreCase: true, out var parsedType)
                    ? parsedType
                    : SqlBackupType.Full;

                var history = await pipeline.ExecuteJobAsync(config, job, progress, sqlType: sqlType);
                Console.WriteLine();

                return history.Status == "SUCCESS" ? 0 : 1;
            }
            else
            {
                Console.WriteLine("Executing batch backup for all enabled jobs...");
                var progress = new Progress<(int cur, int total, BackupProgressInfo info)>(p =>
                {
                    Console.Write($"\rJob {p.cur}/{p.total} [{p.info.Phase,-12}] {p.info.Percent:F0}% {p.info.StatusMessage.PadRight(45)}");
                });

                var results = await batchRunner.RunAllAsync(config, progress);
                Console.WriteLine();

                var failed = results.Count(r => r.Status == "FAILED");
                var partial = results.Count(r => r.Status == "PARTIAL");

                if (failed > 0) return 1;
                if (partial > 0) return config.Schedule.ExitOnPartial;
                return 0;
            }
        }

        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("\nUsage:");
        Console.WriteLine("  UniBackup.Cli.exe --auto                Run all enabled jobs sequentially (Task Scheduler)");
        Console.WriteLine("  UniBackup.Cli.exe --auto --job <id>     Run a specific job by Id");
        Console.WriteLine("  UniBackup.Cli.exe --auto --job <id> --type diff   SQL jobs: full (default) | diff | log");
        Console.WriteLine("  UniBackup.Cli.exe --restore --job <id> --db <name>  Restore SQL database from cloud");
        Console.WriteLine("    [--at <yyyy-MM-dd HH:mm:ss>]     Point-in-time restore target");
        Console.WriteLine("    [--as <newName>]                 Restore under a different database name");
        Console.WriteLine("    [--provider <s3|gdrive>]         Destination to restore from");
        Console.WriteLine("    [--passphrase <pwd>]             Decryption passphrase if encrypted");
        Console.WriteLine("    [--replace]                      Overwrite existing database");
        Console.WriteLine("  UniBackup.Cli.exe --validate            Run comprehensive server & connection diagnostics");
        Console.WriteLine("  UniBackup.Cli.exe --list-jobs           List all configured backup jobs");
        Console.WriteLine("  UniBackup.Cli.exe --service <action>    Manage Windows Service (install, uninstall, start, stop, status)");
        Console.WriteLine("  UniBackup.Cli.exe --help                Display this help screen\n");
    }
}
