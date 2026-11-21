using UniBackup.Core.Common;
using Serilog;
using Serilog.Events;

namespace UniBackup.Infrastructure.Logging;

public static class LoggingSetup
{
    private static bool _initialized;

    public static string ManualLogPath => Path.Combine(AppPaths.DefaultLogsDirectory, "UniBackup_Manual.log");
    public static string ServiceLogPath => Path.Combine(AppPaths.DefaultLogsDirectory, "UniBackup_Service.log");

    public static void Initialize(string? logFilePath = null, int maxLogMb = 10, int keepDays = 30)
    {
        if (_initialized) return;

        EnsureLogFilesExist();

        var targetPath = string.IsNullOrWhiteSpace(logFilePath) ? ManualLogPath : logFilePath;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: targetPath,
                fileSizeLimitBytes: maxLogMb * 1024L * 1024L,
                retainedFileCountLimit: keepDays > 0 ? keepDays : 30,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _initialized = true;
    }

    public static void EnsureLogFilesExist()
    {
        var logDir = AppPaths.DefaultLogsDirectory;
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
            EnsureLogFileExists(ManualLogPath, "Manual Run Log (UI Interactive Sessions)");
            EnsureLogFileExists(ServiceLogPath, "Service Run Log (Windows Service & Task Scheduler)");
        }
    }

    private static void EnsureLogFileExists(string path, string label)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [INF] {label} initialized.{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore concurrent creation issues
        }
    }

    public static string GetPerJobLogPath(string jobId)
    {
        return ManualLogPath;
    }
}
