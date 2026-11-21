using System.Text.Json;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Engine.Services;

public class ConfigurationService
{
    private readonly ICredentialProtector _crypto;
    private readonly string? _dataDirectory; // null = AppPaths data directory; tests pass a temp folder
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ConfigurationService(ICredentialProtector crypto, string? dataDirectory = null)
    {
        _crypto = crypto;
        _dataDirectory = dataDirectory;
    }

    public string DataDirectory => _dataDirectory ?? AppPaths.ResolveDataDirectory();

    private string DataFile(string name, string defaultPath) =>
        _dataDirectory is null ? defaultPath : Path.Combine(_dataDirectory, name);

    public async Task<AppConfig> LoadConfigAsync(string? explicitPath = null)
    {
        var configPath = explicitPath ?? DataFile("config.json", AppPaths.DefaultConfigFile);

        try
        {
            if (!File.Exists(configPath))
            {
                // Attempt legacy import
                var legacyPath = Path.Combine(AppPaths.AppBaseDirectory, "config.json");
                if (File.Exists(legacyPath))
                {
                    Log.Information("Importing legacy config from {Path}", legacyPath);
                    configPath = legacyPath;
                }
                else
                {
                    Log.Information("Config file not found at {Path}. Creating default configuration.", configPath);
                    var defaultConfig = new AppConfig();
                    await SaveConfigAsync(defaultConfig, configPath);
                    return defaultConfig;
                }
            }

            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            
            // Decrypt / unseal secrets
            _crypto.UnsealConfig(config);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load configuration from {Path}. Using default.", configPath);
            return new AppConfig();
        }
    }

    public async Task<Result> SaveConfigAsync(AppConfig config, string? explicitPath = null)
    {
        var configPath = explicitPath ?? DataFile("config.json", AppPaths.DefaultConfigFile);

        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Clone to avoid mutating in-memory plaintext credentials
            var cloneJson = JsonSerializer.Serialize(config, JsonOptions);
            var clone = JsonSerializer.Deserialize<AppConfig>(cloneJson, JsonOptions) ?? new AppConfig();

            // Seal secrets
            _crypto.SealConfig(clone);

            var json = JsonSerializer.Serialize(clone, JsonOptions);
            await File.WriteAllTextAsync(configPath, json);
            Log.Information("Configuration saved successfully to {Path}", configPath);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save configuration to {Path}: {Error}", configPath, ex.Message);
            return Result.Failure($"Save config error: {ex.Message}");
        }
    }

    public async Task<List<BackupJob>> LoadJobsAsync(string? explicitPath = null)
    {
        var jobsPath = explicitPath ?? DataFile("jobs.json", AppPaths.DefaultJobsFile);

        try
        {
            if (!File.Exists(jobsPath))
            {
                var legacyPath = Path.Combine(AppPaths.AppBaseDirectory, "jobs.json");
                if (File.Exists(legacyPath))
                {
                    jobsPath = legacyPath;
                }
                else
                {
                    return [];
                }
            }

            var json = await File.ReadAllTextAsync(jobsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("jobs", out var jobsElement))
            {
                var jobs = JsonSerializer.Deserialize<List<BackupJob>>(jobsElement.GetRawText(), JsonOptions);
                return jobs ?? [];
            }

            return [];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load jobs from {Path}: {Error}", jobsPath, ex.Message);
            return [];
        }
    }

    public async Task<Result> SaveJobsAsync(List<BackupJob> jobs, string? explicitPath = null)
    {
        var jobsPath = explicitPath ?? DataFile("jobs.json", AppPaths.DefaultJobsFile);

        try
        {
            var dir = Path.GetDirectoryName(jobsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var wrapper = new { jobs };
            var json = JsonSerializer.Serialize(wrapper, JsonOptions);
            await File.WriteAllTextAsync(jobsPath, json);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save jobs: {Error}", ex.Message);
            return Result.Failure($"Save jobs error: {ex.Message}");
        }
    }

    public async Task<List<RunHistoryRecord>> LoadHistoryAsync(string? explicitPath = null)
    {
        var historyPath = explicitPath ?? DataFile("history.json", AppPaths.DefaultHistoryFile);

        try
        {
            if (!File.Exists(historyPath)) return [];
            var json = await File.ReadAllTextAsync(historyPath);
            var records = JsonSerializer.Deserialize<List<RunHistoryRecord>>(json, JsonOptions);
            return records ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load history from {Path}", historyPath);
            return [];
        }
    }

    public async Task<Result> AppendHistoryAsync(RunHistoryRecord record, string? explicitPath = null)
    {
        var historyPath = explicitPath ?? DataFile("history.json", AppPaths.DefaultHistoryFile);

        try
        {
            var history = await LoadHistoryAsync(historyPath);
            history.Insert(0, record);

            // Keep max 1000 history entries
            if (history.Count > 1000)
            {
                history = history.Take(1000).ToList();
            }

            var dir = Path.GetDirectoryName(historyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(history, JsonOptions);
            await File.WriteAllTextAsync(historyPath, json);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to append history record: {Error}", ex.Message);
            return Result.Failure($"History error: {ex.Message}");
        }
    }
}
