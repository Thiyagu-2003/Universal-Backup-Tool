using UniBackup.Core.Models;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Security;
using Xunit;

namespace UniBackup.Tests;

public class ConfigurationServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly ConfigurationService _configService;
    private readonly CredentialProtector _crypto = new();

    public ConfigurationServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"CloudPacker_ConfigTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _configService = new ConfigurationService(_crypto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public async Task Test_Config_Save_Seals_And_Load_Unseals()
    {
        var configPath = Path.Combine(_testDir, "config.json");
        var cfg = new AppConfig();
        cfg.Aws.AccessKeyId = "AKIA1234567890EXAMPLE";
        cfg.Aws.SecretAccessKey = "secret_key_value_xyz";
        cfg.Email.Password = "my_email_pass_123";

        // 1. Save config
        var saveResult = await _configService.SaveConfigAsync(cfg, configPath);
        Assert.True(saveResult.IsSuccess);

        // 2. Inspect raw file: secrets must be sealed with "enc:"
        var rawJson = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("AKIA1234567890EXAMPLE", rawJson);
        Assert.DoesNotContain("secret_key_value_xyz", rawJson);
        Assert.DoesNotContain("my_email_pass_123", rawJson);
        Assert.Contains("enc:", rawJson);

        // 3. Load config: secrets must be decrypted in memory
        var loaded = await _configService.LoadConfigAsync(configPath);
        Assert.Equal("AKIA1234567890EXAMPLE", loaded.Aws.AccessKeyId);
        Assert.Equal("secret_key_value_xyz", loaded.Aws.SecretAccessKey);
        Assert.Equal("my_email_pass_123", loaded.Email.Password);
    }

    [Fact]
    public async Task Test_Jobs_RoundTrip()
    {
        var jobsPath = Path.Combine(_testDir, "jobs.json");
        var jobs = new List<BackupJob>
        {
            new()
            {
                Id = "job_01",
                Name = "SiteA",
                SourceFolder = @"C:\inetpub\wwwroot\siteA",
                Destinations = ["s3", "gdrive"],
                UseVss = true,
                Schedule = new JobSchedule { Freq = "weekly", Days = ["MON", "FRI"], Time = "01:30" }
            }
        };

        var saveRes = await _configService.SaveJobsAsync(jobs, jobsPath);
        Assert.True(saveRes.IsSuccess);

        var loaded = await _configService.LoadJobsAsync(jobsPath);
        Assert.Single(loaded);
        Assert.Equal("SiteA", loaded[0].Name);
        Assert.True(loaded[0].UseVss);
        Assert.Equal("weekly", loaded[0].Schedule.Freq);
        Assert.Equal(2, loaded[0].Schedule.Days.Count);
    }

    [Fact]
    public async Task Test_History_Append_And_Limit()
    {
        var historyPath = Path.Combine(_testDir, "history.json");

        var rec1 = new RunHistoryRecord { JobId = "1", JobName = "Job 1", Status = "SUCCESS" };
        var rec2 = new RunHistoryRecord { JobId = "2", JobName = "Job 2", Status = "FAILED" };

        await _configService.AppendHistoryAsync(rec1, historyPath);
        await _configService.AppendHistoryAsync(rec2, historyPath);

        var list = await _configService.LoadHistoryAsync(historyPath);
        Assert.Equal(2, list.Count);
        // Latest record must be at index 0
        Assert.Equal("Job 2", list[0].JobName);
        Assert.Equal("Job 1", list[1].JobName);
    }
}
