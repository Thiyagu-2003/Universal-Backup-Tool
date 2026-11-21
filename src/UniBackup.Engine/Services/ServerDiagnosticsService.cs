using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Serilog;

namespace UniBackup.Engine.Services;

public class DiagnosticItem
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "PASS"; // PASS, WARN, FAIL
    public string Details { get; set; } = "";
}

public class ServerDiagnosticsService
{
    private readonly IEnumerable<IStorageProvider> _storageProviders;
    private readonly INotificationService _notificationService;
    private readonly IIisDiscoveryService _iisService;
    private readonly ConfigurationService _configService;

    public ServerDiagnosticsService(
        IEnumerable<IStorageProvider> storageProviders,
        INotificationService notificationService,
        IIisDiscoveryService iisService,
        ConfigurationService configService)
    {
        _storageProviders = storageProviders;
        _notificationService = notificationService;
        _iisService = iisService;
        _configService = configService;
    }

    public async Task<List<DiagnosticItem>> RunDiagnosticsAsync(AppConfig config, CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();

        // 1. Temp folder check
        var tempFolder = config.Backup.TempFolder;
        var tempWritable = AppPaths.EnsureWritableDirectory(tempFolder, out _);
        var tempFree = AppPaths.GetFreeDiskSpaceBytes(tempFolder);

        if (!tempWritable)
        {
            items.Add(new DiagnosticItem
            {
                Category = "Storage",
                Name = "Temp Folder Writable",
                Status = "FAIL",
                Details = $"Cannot write to configured temp directory: {tempFolder}"
            });
        }
        else
        {
            items.Add(new DiagnosticItem
            {
                Category = "Storage",
                Name = "Temp Folder Free Space",
                Status = tempFree < 1024L * 1024L * 1024L ? "WARN" : "PASS",
                Details = $"{FormattingUtils.FormatBytes(tempFree)} free on {tempFolder}"
            });
        }

        // 2. System drive check (matches Python validate_server)
        try
        {
            var sysTemp = Path.GetTempPath();
            var sysFree = AppPaths.GetFreeDiskSpaceBytes(sysTemp);
            items.Add(new DiagnosticItem
            {
                Category = "Storage",
                Name = "System Drive Free Space",
                Status = sysFree < 1024L * 1024L * 1024L ? "WARN" : "PASS",
                Details = $"{FormattingUtils.FormatBytes(sysFree)} free on system drive ({sysTemp})"
            });
        }
        catch { }

        // 3. AWS S3 check
        if (config.Aws.Enabled)
        {
            var s3 = _storageProviders.FirstOrDefault(p => p.Name == "s3");
            if (s3 != null)
            {
                var s3Result = await s3.ValidateConnectionAsync(config, ct);
                items.Add(new DiagnosticItem
                {
                    Category = "Cloud",
                    Name = "AWS S3 Connection",
                    Status = s3Result.IsSuccess ? "PASS" : "FAIL",
                    Details = s3Result.IsSuccess ? $"Successfully connected to bucket '{config.Aws.BucketName}'" : s3Result.Error ?? "Error"
                });
            }
        }

        // 4. Google Drive check
        if (config.GDrive.Enabled)
        {
            var gdrive = _storageProviders.FirstOrDefault(p => p.Name == "gdrive");
            if (gdrive != null)
            {
                var gdResult = await gdrive.ValidateConnectionAsync(config, ct);
                items.Add(new DiagnosticItem
                {
                    Category = "Cloud",
                    Name = "Google Drive Auth",
                    Status = gdResult.IsSuccess ? "PASS" : "WARN",
                    Details = gdResult.IsSuccess ? "Authenticated & refresh token valid" : gdResult.Error ?? "Auth missing"
                });
            }
        }

        // 5. Email setup check (validates configuration without sending real mail, matches Python validate_server)
        if (!config.Email.Enabled)
        {
            items.Add(new DiagnosticItem
            {
                Category = "Alerts",
                Name = "Email Alerts Configuration",
                Status = "WARN",
                Details = "Email reports are currently disabled in Settings."
            });
        }
        else if (config.Email.ToAddresses == null || config.Email.ToAddresses.Count == 0 || string.IsNullOrWhiteSpace(config.Email.ToAddresses[0]))
        {
            items.Add(new DiagnosticItem
            {
                Category = "Alerts",
                Name = "Email Alerts Configuration",
                Status = "WARN",
                Details = "No recipient email addresses set (Settings → Notifications)."
            });
        }
        else if (string.IsNullOrWhiteSpace(config.Email.FromAddress))
        {
            items.Add(new DiagnosticItem
            {
                Category = "Alerts",
                Name = "Email Alerts Configuration",
                Status = "WARN",
                Details = "No 'From' sender address configured."
            });
        }
        else if (string.IsNullOrWhiteSpace(config.Email.SmtpHost))
        {
            items.Add(new DiagnosticItem
            {
                Category = "Alerts",
                Name = "Email Alerts Configuration",
                Status = "WARN",
                Details = "No SMTP server host configured."
            });
        }
        else
        {
            items.Add(new DiagnosticItem
            {
                Category = "Alerts",
                Name = "Email Alerts Configuration",
                Status = "PASS",
                Details = $"{config.Email.SmtpHost}:{config.Email.SmtpPort} → {string.Join(", ", config.Email.ToAddresses)} (Use 'Send Test Email Alert' to test delivery)"
            });
        }

        // 5. Job Source Folders Check
        var jobs = await _configService.LoadJobsAsync();
        foreach (var job in jobs)
        {
            var exists = Directory.Exists(job.EffectiveSourcePath);
            items.Add(new DiagnosticItem
            {
                Category = "Jobs",
                Name = $"Job: {job.Name}",
                Status = exists ? "PASS" : "FAIL",
                Details = exists ? $"Source path verified: {job.EffectiveSourcePath}" : $"Source path missing: {job.EffectiveSourcePath}"
            });
        }

        // 6. IIS Discovery Check
        var iisResult = await _iisService.DiscoverSitesAsync();
        items.Add(new DiagnosticItem
        {
            Category = "System",
            Name = "IIS Integration",
            Status = iisResult.IsSuccess ? "PASS" : "WARN",
            Details = iisResult.IsSuccess ? $"Found {iisResult.Value.Count} hosted IIS websites" : iisResult.Error ?? "IIS not detected"
        });

        return items;
    }
}
