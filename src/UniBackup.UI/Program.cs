using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
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
using UniBackup.Service;
using UniBackup.UI.ViewModels;
using UniBackup.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace UniBackup.UI;

public static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static void EnsureConsoleOutput()
    {
        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                var outHandle = GetStdHandle(StdOutputHandle);
                if (outHandle != IntPtr.Zero && outHandle != new IntPtr(-1))
                {
                    var safeOut = new Microsoft.Win32.SafeHandles.SafeFileHandle(outHandle, ownsHandle: false);
                    var fsOut = new FileStream(safeOut, FileAccess.Write);
                    var writer = new StreamWriter(fsOut, System.Text.Encoding.UTF8) { AutoFlush = true };
                    Console.SetOut(writer);
                }

                var errHandle = GetStdHandle(StdErrorHandle);
                if (errHandle != IntPtr.Zero && errHandle != new IntPtr(-1))
                {
                    var safeErr = new Microsoft.Win32.SafeHandles.SafeFileHandle(errHandle, ownsHandle: false);
                    var fsErr = new FileStream(safeErr, FileAccess.Write);
                    var writerErr = new StreamWriter(fsErr, System.Text.Encoding.UTF8) { AutoFlush = true };
                    Console.SetError(writerErr);
                }
            }
            catch
            {
                // Ignore fallback errors
            }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // 1. If invoked as Windows Service worker
        if (args.Contains("--service-worker"))
        {
            return RunWindowsServiceWorker(args);
        }

        // 2. If CLI arguments passed, attach to parent console so Console.WriteLine outputs to terminal
        if (args.Length > 0)
        {
            EnsureConsoleOutput();
            return RunCliModeAsync(args).GetAwaiter().GetResult();
        }

        // 3. Normal GUI launch (double-clicked in Explorer or launched without args)
        try
        {
            var app = new App();
            app.InitializeComponent();

            var crypto = new CredentialProtector();
            var configService = new ConfigurationService(crypto);
            var config = configService.LoadConfigAsync().GetAwaiter().GetResult();

            // Initialize the dedicated manual run log stream for GUI interactive use
            LoggingSetup.Initialize(LoggingSetup.ManualLogPath, config.Backup.MaxLogMb, config.Backup.KeepLogsDays);

            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Prompt administrator login dialog
            if (config.Security?.RequireLogin != false)
            {
                var loginVm = new LoginViewModel(configService);
                var loginWindow = new LoginWindow(loginVm);
                var loginResult = loginWindow.ShowDialog();
                if (loginResult != true && !loginVm.IsAuthenticated)
                {
                    app.Shutdown(0);
                    return 0;
                }
            }

            // Once signed in, open MainWindow maximized (Full Screen)
            var mainWindow = new MainWindow();
            mainWindow.WindowState = WindowState.Maximized;
            app.MainWindow = mainWindow;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            return app.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Startup Error: {ex.Message}\n\n{ex.StackTrace}", "UniBackup Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static int RunWindowsServiceWorker(string[] args)
    {
        var crypto = new CredentialProtector();
        var configService = new ConfigurationService(crypto);
        var config = configService.LoadConfigAsync().GetAwaiter().GetResult();
        LoggingSetup.Initialize(LoggingSetup.ServiceLogPath, config.Backup.MaxLogMb, config.Backup.KeepLogsDays);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = WindowsServiceManager.ServiceName;
        });

        builder.Services.AddSingleton<ICredentialProtector>(crypto);
        builder.Services.AddSingleton(configService);
        builder.Services.AddSingleton<IArchiveService, HighSpeedZipArchiveService>();
        builder.Services.AddSingleton<IStorageProvider, AwsS3StorageProvider>();
        builder.Services.AddSingleton<IStorageProvider, GoogleDriveStorageProvider>();
        builder.Services.AddSingleton<INotificationService, EmailNotificationService>();
        builder.Services.AddSingleton<WebhookNotificationService>();
        builder.Services.AddSingleton<SqlServerBackupService>();
        builder.Services.AddSingleton<BackupPipeline>();
        builder.Services.AddSingleton<BatchBackupRunner>();
        builder.Services.AddHostedService<CloudPackerWorker>();

        var host = builder.Build();
        host.Run();
        return 0;
    }

    private static async Task<int> RunCliModeAsync(string[] args)
    {
        var crypto = new CredentialProtector();
        var configService = new ConfigurationService(crypto);
        var config = await configService.LoadConfigAsync();
        LoggingSetup.Initialize(LoggingSetup.ServiceLogPath, config.Backup.MaxLogMb, config.Backup.KeepLogsDays);

        var archiveService = new HighSpeedZipArchiveService();
        var s3Storage = new AwsS3StorageProvider();
        var gdriveStorage = new GoogleDriveStorageProvider();
        var storageProviders = new IStorageProvider[] { s3Storage, gdriveStorage };
        var emailService = new EmailNotificationService();
        var webhookService = new WebhookNotificationService();
        var iisService = new IisSiteDiscoveryService();

        var pipeline = new BackupPipeline(archiveService, storageProviders, emailService, webhookService, configService, new SqlServerBackupService(crypto));
        var batchRunner = new BatchBackupRunner(pipeline, configService, emailService);
        var diagnostics = new ServerDiagnosticsService(storageProviders, emailService, iisService, configService);

        Console.WriteLine($"\nUniBackup Pro - Host: {Environment.MachineName}");
        Console.WriteLine("=========================================================");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var isJson = args.Contains("--json");

        if (args.Contains("--export-config"))
        {
            var pIndex = Array.IndexOf(args, "--export-config");
            var targetPath = pIndex < args.Length - 1 ? args[pIndex + 1] : "UniBackup_Config.ubcfg";
            var passIndex = Array.IndexOf(args, "--passphrase");
            var passphrase = passIndex >= 0 && passIndex < args.Length - 1 ? args[passIndex + 1] : "";

            if (string.IsNullOrEmpty(passphrase))
            {
                Console.Error.WriteLine("Error: --export-config requires --passphrase <pwd> to encrypt secrets.");
                return 1;
            }

            var exportService = new ConfigExportService(configService, crypto);
            var res = await exportService.ExportConfigAsync(targetPath, passphrase);
            if (res.IsSuccess)
            {
                if (isJson) Console.WriteLine(JsonSerializer.Serialize(new { status = "SUCCESS", path = targetPath }));
                else Console.WriteLine($"[SUCCESS] Configuration securely exported to: {targetPath}");
                return 0;
            }
            if (isJson) Console.WriteLine(JsonSerializer.Serialize(new { status = "FAILED", error = res.Error }));
            else Console.Error.WriteLine($"[FAILED] Export failed: {res.Error}");
            return 1;
        }

        if (args.Contains("--import-config"))
        {
            var pIndex = Array.IndexOf(args, "--import-config");
            var sourcePath = pIndex < args.Length - 1 ? args[pIndex + 1] : "UniBackup_Config.ubcfg";
            var passIndex = Array.IndexOf(args, "--passphrase");
            var passphrase = passIndex >= 0 && passIndex < args.Length - 1 ? args[passIndex + 1] : "";

            if (string.IsNullOrEmpty(passphrase))
            {
                Console.Error.WriteLine("Error: --import-config requires --passphrase <pwd>.");
                return 1;
            }

            var exportService = new ConfigExportService(configService, crypto);
            var res = await exportService.ImportConfigAsync(sourcePath, passphrase);
            if (res.IsSuccess)
            {
                if (isJson) Console.WriteLine(JsonSerializer.Serialize(new { status = "SUCCESS", jobsImported = res.Value!.Jobs.Count }));
                else Console.WriteLine($"[SUCCESS] Imported {res.Value!.Jobs.Count} jobs and configuration from: {sourcePath}");
                return 0;
            }
            if (isJson) Console.WriteLine(JsonSerializer.Serialize(new { status = "FAILED", error = res.Error }));
            else Console.Error.WriteLine($"[FAILED] Import failed: {res.Error}");
            return 1;
        }

        if (args.Contains("--history"))
        {
            var jobIndex = Array.IndexOf(args, "--job");
            var jobId = jobIndex >= 0 && jobIndex < args.Length - 1 ? args[jobIndex + 1] : null;
            var limitIndex = Array.IndexOf(args, "--limit");
            var limit = limitIndex >= 0 && limitIndex < args.Length - 1 && int.TryParse(args[limitIndex + 1], out var parsedLimit) ? parsedLimit : 50;

            var runs = await configService.HistoryRepository.GetRunsAsync(limit, jobId);
            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            Console.WriteLine($"\nBackup History ({runs.Count} runs):");
            Console.WriteLine("--------------------------------------------------------------------------------");
            foreach (var r in runs)
            {
                Console.WriteLine($"[{r.Timestamp:yyyy-MM-dd HH:mm}] {r.JobName,-20} | {r.Status,-7} | {r.DurationSeconds,5:F1}s | {FormattingUtils.FormatBytes(r.ArchiveSizeBytes),-10} | {r.ErrorMessage ?? "OK"}");
            }
            return 0;
        }

        if (args.Contains("--service"))
        {
            var sIndex = Array.IndexOf(args, "--service");
            var subCommand = sIndex < args.Length - 1 ? args[sIndex + 1].ToLowerInvariant() : "status";

            switch (subCommand)
            {
                case "install":
                    // When installing, set service binPath to launch with --service-worker
                    var currentExe = Environment.ProcessPath ?? Path.Combine(AppPaths.AppBaseDirectory, "UniBackup.exe");
                    var serviceBinPath = $"\\\"{currentExe}\\\" --service-worker";
                    var instRes = WindowsServiceManager.InstallService(serviceBinPath);
                    Console.WriteLine(instRes.IsSuccess ? "Windows Service 'UniBackup' installed successfully." : $"Error: {instRes.Error}");
                    return instRes.IsSuccess ? 0 : 1;

                case "uninstall":
                    var unRes = WindowsServiceManager.UninstallService();
                    Console.WriteLine(unRes.IsSuccess ? "Windows Service 'UniBackup' uninstalled." : $"Error: {unRes.Error}");
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
                Console.WriteLine("Usage: UniBackup.exe --restore --job <id> --db <name> [--at <yyyy-MM-dd HH:mm:ss>] [--as <newName>] [--provider <s3|gdrive>] [--passphrase <pwd>] [--replace]");
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
        Console.WriteLine("  UniBackup.exe                           Launch Modern WPF Desktop Dashboard");
        Console.WriteLine("  UniBackup.exe --auto                    Run all enabled jobs (Task Scheduler)");
        Console.WriteLine("  UniBackup.exe --auto --job <id>         Run specific backup job by ID");
        Console.WriteLine("  UniBackup.exe --auto --job <id> --type diff   SQL jobs: full (default) | diff | log");
        Console.WriteLine("  UniBackup.exe --restore --job <id> --db <name>  Restore SQL database from cloud");
        Console.WriteLine("    [--at <yyyy-MM-dd HH:mm:ss>]     Point-in-time restore target");
        Console.WriteLine("    [--as <newName>]                 Restore under a different database name");
        Console.WriteLine("    [--provider <s3|gdrive>]         Destination to restore from");
        Console.WriteLine("    [--passphrase <pwd>]             Decryption passphrase if encrypted");
        Console.WriteLine("    [--replace]                      Overwrite existing database");
        Console.WriteLine("  UniBackup.exe --service <action>        Manage Windows Service (install, uninstall, start, stop, status)");
        Console.WriteLine("  UniBackup.exe --export-config <file> --passphrase <pwd>  Export encrypted config");
        Console.WriteLine("  UniBackup.exe --import-config <file> --passphrase <pwd>  Import encrypted config");
        Console.WriteLine("  UniBackup.exe --history [--job <id>] [--limit <n>] [--json]  View execution history");
        Console.WriteLine("  UniBackup.exe --validate                Run server and connection diagnostics");
        Console.WriteLine("  UniBackup.exe --list-jobs               List configured backup jobs");
        Console.WriteLine("  UniBackup.exe --help                    Show this help screen\n");
    }
}
