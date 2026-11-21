using System.Text.Json;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Security;

namespace UniBackup.Engine.Services;

public class ConfigExportBundle
{
    public string Version { get; set; } = "2.0";
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public string ExportedFromMachine { get; set; } = Environment.MachineName;
    public AppConfig Config { get; set; } = new();
    public List<BackupJob> Jobs { get; set; } = [];
}

public class ConfigExportService
{
    private readonly ConfigurationService _configService;
    private readonly ICredentialProtector _crypto;
    private readonly StreamedEncryptionService _encryption = new();

    public ConfigExportService(ConfigurationService configService, ICredentialProtector crypto)
    {
        _configService = configService;
        _crypto = crypto;
    }

    public async Task<Result> ExportConfigAsync(
        string targetUbcfgPath,
        string passphrase,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return Result.Failure("An export passphrase is required to securely encrypt secrets.");
        }

        try
        {
            var config = await _configService.LoadConfigAsync();
            var jobs = await _configService.LoadJobsAsync();

            var bundle = new ConfigExportBundle
            {
                Config = config,
                Jobs = jobs
            };

            var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
            var tempJson = Path.Combine(Path.GetTempPath(), $"ub_export_{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempJson, json, ct);

            try
            {
                var dir = Path.GetDirectoryName(targetUbcfgPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                await _encryption.EncryptFileAsync(tempJson, targetUbcfgPath, passphrase, ct);
                Log.Information("[Config Export] Exported configuration to {Path} encrypted with AES-256-GCM", targetUbcfgPath);
                return Result.Success();
            }
            finally
            {
                if (File.Exists(tempJson)) File.Delete(tempJson);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Config Export] Failed exporting configuration");
            return Result.Failure($"Export failed: {ex.Message}");
        }
    }

    public async Task<Result<ConfigExportBundle>> ImportConfigAsync(
        string sourceUbcfgPath,
        string passphrase,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceUbcfgPath))
        {
            return Result<ConfigExportBundle>.Failure($"File not found: {sourceUbcfgPath}");
        }

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return Result<ConfigExportBundle>.Failure("Passphrase is required to decrypt the configuration.");
        }

        var tempJson = Path.Combine(Path.GetTempPath(), $"ub_import_{Guid.NewGuid():N}.tmp");
        try
        {
            await _encryption.DecryptFileAsync(sourceUbcfgPath, tempJson, passphrase, ct);
            var json = await File.ReadAllTextAsync(tempJson, ct);

            var bundle = JsonSerializer.Deserialize<ConfigExportBundle>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (bundle == null)
            {
                return Result<ConfigExportBundle>.Failure("Deserialization of configuration bundle returned null.");
            }

            // Save config and jobs into local machine
            await _configService.SaveConfigAsync(bundle.Config);
            await _configService.SaveJobsAsync(bundle.Jobs);

            Log.Information("[Config Import] Successfully imported {JobCount} jobs and configuration from {Path}",
                bundle.Jobs.Count, sourceUbcfgPath);

            return Result<ConfigExportBundle>.Success(bundle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Config Import] Failed importing configuration from {Path}", sourceUbcfgPath);
            return Result<ConfigExportBundle>.Failure($"Import failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }
}
