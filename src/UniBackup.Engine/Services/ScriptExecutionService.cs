using System.Diagnostics;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Sql;

namespace UniBackup.Engine.Services;

public class ScriptExecutionService
{
    private readonly SqlServerBackupService _sqlService;

    public ScriptExecutionService(SqlServerBackupService sqlService)
    {
        _sqlService = sqlService;
    }

    public async Task<Result<string>> ExecuteScriptAsync(
        BackupScriptConfig script,
        BackupJob job,
        SqlBackupType? currentBackupType,
        CancellationToken ct = default)
    {
        if (!script.Enabled || string.IsNullOrWhiteSpace(script.CommandOrPath))
        {
            return Result<string>.Success("Script disabled or empty.");
        }

        if (script.FullBackupsOnly)
        {
            var isFull = currentBackupType == null || currentBackupType == SqlBackupType.Full;
            if (!isFull)
            {
                Log.Information("[Script] Skipping script '{Cmd}' because it is marked for FULL backups only", script.CommandOrPath);
                return Result<string>.Success("Skipped (Full backups only).");
            }
        }

        var scriptType = script.ScriptType.ToLowerInvariant();
        Log.Information("[Script] Executing {Type} script for job '{Job}': {Cmd}", scriptType, job.Name, script.CommandOrPath);

        if (scriptType == "sql")
        {
            if (job.Sql == null)
            {
                return Result<string>.Failure("SQL script cannot run: job has no SQL Server configuration.");
            }

            var sqlResult = await _sqlService.RunAsync(job.Sql, script.CommandOrPath, null, ct);
            return sqlResult.IsSuccess
                ? Result<string>.Success("SQL Script executed successfully.")
                : Result<string>.Failure($"SQL Script error: {sqlResult.Error}");
        }

        // Process-based scripts: powershell or batch
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (scriptType == "powershell")
        {
            psi.FileName = "powershell.exe";
            if (File.Exists(script.CommandOrPath))
            {
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script.CommandOrPath}\"";
            }
            else
            {
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.CommandOrPath}\"";
            }
        }
        else // batch / cmd
        {
            psi.FileName = "cmd.exe";
            if (File.Exists(script.CommandOrPath))
            {
                psi.Arguments = $"/c \"{script.CommandOrPath}\"";
            }
            else
            {
                psi.Arguments = $"/c {script.CommandOrPath}";
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var proc = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutSec = script.TimeoutSeconds > 0 ? script.TimeoutSeconds : 300;
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            var combinedOutput = $"{stdout}\n{stderr}".Trim();
            if (proc.ExitCode == 0)
            {
                Log.Information("[Script] Script completed successfully in {Duration:F1}s. Output:\n{Output}",
                    sw.Elapsed.TotalSeconds, combinedOutput);
                return Result<string>.Success(combinedOutput);
            }

            Log.Warning("[Script] Script exited with error code {ExitCode} in {Duration:F1}s. Output:\n{Output}",
                proc.ExitCode, sw.Elapsed.TotalSeconds, combinedOutput);

            return Result<string>.Failure($"Script exited with code {proc.ExitCode}: {combinedOutput}");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return Result<string>.Failure($"Script timed out after {script.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Result<string>.Failure($"Script execution error: {ex.Message}");
        }
    }
}
