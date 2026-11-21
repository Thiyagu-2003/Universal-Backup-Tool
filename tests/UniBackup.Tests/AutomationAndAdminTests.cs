using UniBackup.Core.Common;
using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using Xunit;

namespace UniBackup.Tests;

public class AutomationAndAdminTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConfigurationService _configService;
    private readonly CredentialProtector _crypto = new();

    public AutomationAndAdminTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"UniBackup_AdminTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _configService = new ConfigurationService(_crypto, _testDir);
    }

    [Fact]
    public async Task ScriptExecution_PowerShellCommand_ExecutesAndCapturesOutput()
    {
        var sqlBackup = new SqlServerBackupService(_crypto);
        var scriptService = new ScriptExecutionService(sqlBackup);

        var script = new BackupScriptConfig
        {
            Enabled = true,
            ScriptType = "powershell",
            CommandOrPath = "Write-Output 'UniBackup_PreScript_Output'",
            TimeoutSeconds = 10,
            AbortOnError = true
        };

        var job = new BackupJob { Name = "Script Job", Kind = "folder" };
        var result = await scriptService.ExecuteScriptAsync(script, job, SqlBackupType.Full);

        Assert.True(result.IsSuccess);
        Assert.Contains("UniBackup_PreScript_Output", result.Value);
    }

    [Fact]
    public async Task ScriptExecution_FullBackupsOnly_SkipsOnDifferential()
    {
        var sqlBackup = new SqlServerBackupService(_crypto);
        var scriptService = new ScriptExecutionService(sqlBackup);

        var script = new BackupScriptConfig
        {
            Enabled = true,
            ScriptType = "powershell",
            CommandOrPath = "exit 1", // Would fail if executed
            FullBackupsOnly = true,
            AbortOnError = true
        };

        var job = new BackupJob { Name = "Diff Job", Kind = "sqlserver" };
        var result = await scriptService.ExecuteScriptAsync(script, job, SqlBackupType.Diff);

        Assert.True(result.IsSuccess);
        Assert.Contains("Skipped", result.Value);
    }

    [Fact]
    public async Task ScriptExecution_FailingCommand_ReturnsFailure()
    {
        var sqlBackup = new SqlServerBackupService(_crypto);
        var scriptService = new ScriptExecutionService(sqlBackup);

        var script = new BackupScriptConfig
        {
            Enabled = true,
            ScriptType = "powershell",
            CommandOrPath = "throw 'Simulated pre-script failure'",
            TimeoutSeconds = 10,
            AbortOnError = true
        };

        var job = new BackupJob { Name = "Fail Job", Kind = "folder" };
        var result = await scriptService.ExecuteScriptAsync(script, job, SqlBackupType.Full);

        Assert.False(result.IsSuccess);
        Assert.Contains("Simulated pre-script failure", result.Error);
    }

    [Fact]
    public async Task ConfigExport_And_Import_RoundTrip_WithEncryption()
    {
        // 1. Setup sample configuration and jobs
        var config = new AppConfig
        {
            Aws = new AwsSettings { BucketName = "export-test-bucket", Region = "us-east-1" }
        };
        await _configService.SaveConfigAsync(config);

        var job = new BackupJob
        {
            Id = "exported-job-1",
            Name = "Exported Job",
            Kind = "sqlserver",
            Sql = new SqlServerSource { Server = "sql.corp.internal", Databases = ["Payroll"] }
        };
        await _configService.SaveJobsAsync([job]);

        var exportService = new ConfigExportService(_configService, _crypto);
        var ubcfgFile = Path.Combine(_testDir, "exported_backup.ubcfg");
        var passphrase = "StrongPassphrase_2026!";

        // 2. Export
        var exportRes = await exportService.ExportConfigAsync(ubcfgFile, passphrase);
        Assert.True(exportRes.IsSuccess);
        Assert.True(File.Exists(ubcfgFile));

        // 3. Import with wrong passphrase fails
        var wrongImportRes = await exportService.ImportConfigAsync(ubcfgFile, "WrongPassword");
        Assert.False(wrongImportRes.IsSuccess);

        // 4. Import on a new target directory
        var newTargetDir = Path.Combine(_testDir, "ImportTarget");
        Directory.CreateDirectory(newTargetDir);
        var targetConfigService = new ConfigurationService(_crypto, newTargetDir);
        var targetExportService = new ConfigExportService(targetConfigService, _crypto);

        var importRes = await targetExportService.ImportConfigAsync(ubcfgFile, passphrase);
        Assert.True(importRes.IsSuccess);

        var importedJobs = await targetConfigService.LoadJobsAsync();
        Assert.Single(importedJobs);
        Assert.Equal("exported-job-1", importedJobs[0].Id);
        Assert.Equal("sql.corp.internal", importedJobs[0].Sql?.Server);

        var importedConfig = await targetConfigService.LoadConfigAsync();
        Assert.Equal("export-test-bucket", importedConfig.Aws.BucketName);
    }

    [Fact]
    public async Task WindowsImpersonator_DisabledCredentials_ExecutesGracefully()
    {
        var creds = new WindowsRunAsCredentials { Enabled = false };
        var executed = false;

        await WindowsImpersonator.RunAsAsync(creds, _crypto, () =>
        {
            executed = true;
            return Task.FromResult(0);
        });

        Assert.True(executed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
