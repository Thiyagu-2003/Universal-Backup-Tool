using System.Diagnostics;
using System.Security.Principal;
using UniBackup.Core.Common;
using Serilog;

namespace UniBackup.Infrastructure.Windows;

public static class WindowsServiceManager
{
    public const string ServiceName = "UniBackup";
    public const string DisplayName = "UniBackup Backup Automation Service";
    public const string Description = "Automated high-performance backup engine for zipping and uploading server files to AWS S3 and Google Drive.";

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string GetServiceStatus()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query \"{ServiceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            var error = proc?.StandardError.ReadToEnd() ?? "";
            proc?.WaitForExit(3000);

            if (output.Contains("1060") || error.Contains("1060"))
                return "Not Installed";

            if (output.Contains("RUNNING"))
                return "Running";

            if (output.Contains("STOPPED"))
                return "Stopped";

            if (output.Contains("START_PENDING"))
                return "Starting";

            if (output.Contains("STOP_PENDING"))
                return "Stopping";

            if (output.Contains("PAUSED"))
                return "Paused";

            return proc?.ExitCode == 0 ? "Installed" : "Not Installed";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public static Result InstallService(string? customExePath = null)
    {
        if (!IsElevated())
            return Result.Failure("Administrator privileges are required to install a Windows Service. Please run as Administrator.");

        var exePath = customExePath;
        if (string.IsNullOrEmpty(exePath))
        {
            var baseDir = AppPaths.AppBaseDirectory;
            var serviceCandidate = Path.Combine(baseDir, "UniBackup.Service.exe");
            if (File.Exists(serviceCandidate))
            {
                exePath = serviceCandidate;
            }
            else
            {
                exePath = Path.Combine(baseDir, "UniBackup.exe");
            }
        }

        if (!File.Exists(exePath))
            return Result.Failure($"Service executable not found: {exePath}");

        try
        {
            // 1. Create service with sc.exe
            var createArgs = $"create \"{ServiceName}\" binPath= \"\\\"{exePath}\\\"\" start= auto DisplayName= \"{DisplayName}\"";
            var createPsi = new ProcessStartInfo("sc.exe", createArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var createProc = Process.Start(createPsi);
            createProc?.WaitForExit(10000);

            if (createProc?.ExitCode != 0)
            {
                var err = createProc?.StandardError.ReadToEnd();
                var output = createProc?.StandardOutput.ReadToEnd();
                return Result.Failure($"Failed to create service: {err} {output}");
            }

            // 2. Set description
            var descArgs = $"description \"{ServiceName}\" \"{Description}\"";
            using var descProc = Process.Start(new ProcessStartInfo("sc.exe", descArgs) { CreateNoWindow = true });
            descProc?.WaitForExit(5000);

            // 3. Set failure recovery: auto-restart after 1 minute
            var failureArgs = $"failure \"{ServiceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000";
            using var failProc = Process.Start(new ProcessStartInfo("sc.exe", failureArgs) { CreateNoWindow = true });
            failProc?.WaitForExit(5000);

            Log.Information("Windows Service '{ServiceName}' installed successfully.", ServiceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to install Windows Service {ServiceName}: {Error}", ServiceName, ex.Message);
            return Result.Failure($"Service installation error: {ex.Message}");
        }
    }

    public static Result UninstallService()
    {
        if (!IsElevated())
            return Result.Failure("Administrator privileges are required to uninstall a Windows Service.");

        try
        {
            StopService();

            var psi = new ProcessStartInfo("sc.exe", $"delete \"{ServiceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            if (proc?.ExitCode != 0)
            {
                var err = proc?.StandardError.ReadToEnd();
                return Result.Failure($"Failed to delete service: {err}");
            }

            Log.Information("Windows Service '{ServiceName}' uninstalled successfully.", ServiceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall service: {Error}", ex.Message);
            return Result.Failure($"Service uninstall error: {ex.Message}");
        }
    }

    public static Result StartService()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"start \"{ServiceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            if (proc?.ExitCode != 0)
            {
                var err = proc?.StandardError.ReadToEnd();
                var outStr = proc?.StandardOutput.ReadToEnd();
                return Result.Failure($"Failed to start service: {err} {outStr}");
            }

            Log.Information("Windows Service '{ServiceName}' started.", ServiceName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start service {ServiceName}: {Error}", ServiceName, ex.Message);
            return Result.Failure($"Start service error: {ex.Message}");
        }
    }

    public static Result StopService()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"stop \"{ServiceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop service {ServiceName}: {Error}", ServiceName, ex.Message);
            return Result.Failure($"Stop service error: {ex.Message}");
        }
    }
}
