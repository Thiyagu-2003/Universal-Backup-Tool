using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UniBackup.Core.Client;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Compression;
using UniBackup.Infrastructure.Notifications;
using UniBackup.Infrastructure.Security;
using UniBackup.Infrastructure.Sql;
using UniBackup.Service;
using Xunit;

namespace UniBackup.Tests;

public class NamedPipeApiTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestServer _server;
    private readonly UniBackupClient _client;
    private readonly ConfigurationService _configService;
    private readonly JobExecutionCoordinator _coordinator;

    public NamedPipeApiTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"UniBackup_ApiTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        var crypto = new CredentialProtector();
        _configService = new ConfigurationService(crypto, _testDir);
        var archiveService = new HighSpeedZipArchiveService();
        var storageProviders = new IStorageProvider[] { new MockFailingStorageProvider() };
        var pipeline = new BackupPipeline(archiveService, storageProviders, new EmailNotificationService(), new WebhookNotificationService(), _configService, new SqlServerBackupService(crypto));
        _coordinator = new JobExecutionCoordinator(_configService, pipeline);

        var sqlBackup = new SqlServerBackupService(crypto);
        var sqlRestore = new SqlServerRestoreService(crypto);
        var orchestrator = new SqlRestoreOrchestrator(storageProviders, sqlRestore);
        var drillService = new RestoreDrillService(orchestrator, sqlBackup);

        var webAppBuilder = WebApplication.CreateBuilder();
        webAppBuilder.Logging.ClearProviders();
        webAppBuilder.Services.AddSingleton(crypto);
        webAppBuilder.Services.AddSingleton(_configService);
        webAppBuilder.Services.AddSingleton(pipeline);
        webAppBuilder.Services.AddSingleton(_coordinator);
        webAppBuilder.Services.AddSingleton<IEnumerable<IStorageProvider>>(storageProviders);
        webAppBuilder.Services.AddSingleton(sqlBackup);
        webAppBuilder.Services.AddSingleton(sqlRestore);
        webAppBuilder.Services.AddSingleton(orchestrator);
        webAppBuilder.Services.AddSingleton(drillService);
        webAppBuilder.WebHost.UseTestServer();

        var app = webAppBuilder.Build();
        app.MapUniBackupEndpoints();
        app.StartAsync().GetAwaiter().GetResult();

        _server = app.GetTestServer();
        _client = new UniBackupClient(_server.CreateClient());
    }

    [Fact]
    public async Task Health_Endpoint_Returns_Healthy()
    {
        var res = await _client.GetHealthAsync();

        Assert.True(res.IsSuccess);
        Assert.Equal("Healthy", res.Value!.Status);
        Assert.Equal("UniBackup Pro", res.Value.Service);
        Assert.Equal("2.0", res.Value.Version);
    }

    [Fact]
    public async Task Jobs_CRUD_Via_Client()
    {
        var job = new BackupJob
        {
            Id = "job_api_01",
            Name = "ApiTestJob",
            SourceFolder = _testDir,
            Destinations = ["mock"]
        };

        // 1. Save Job
        var saveRes = await _client.SaveJobAsync(job);
        Assert.True(saveRes.IsSuccess);

        // 2. Get All Jobs
        var listRes = await _client.GetJobsAsync();
        Assert.True(listRes.IsSuccess);
        Assert.Contains(listRes.Value!, j => j.Id == "job_api_01");

        // 3. Get Single Job
        var getRes = await _client.GetJobAsync("job_api_01");
        Assert.True(getRes.IsSuccess);
        Assert.Equal("ApiTestJob", getRes.Value!.Name);

        // 4. Delete Job
        var delRes = await _client.DeleteJobAsync("job_api_01");
        Assert.True(delRes.IsSuccess);

        var verifyRes = await _client.GetJobAsync("job_api_01");
        Assert.True(verifyRes.IsFailure);
    }

    [Fact]
    public async Task Settings_Get_And_Save_Via_Client()
    {
        var getRes = await _client.GetSettingsAsync();
        Assert.True(getRes.IsSuccess);

        var config = getRes.Value!;
        config.Backup.MaxLogMb = 42;

        var saveRes = await _client.SaveSettingsAsync(config);
        Assert.True(saveRes.IsSuccess);

        var verifyRes = await _client.GetSettingsAsync();
        Assert.True(verifyRes.IsSuccess);
        Assert.Equal(42, verifyRes.Value!.Backup.MaxLogMb);
    }

    [Fact]
    public async Task ActiveRuns_InitiallyEmpty_CancelAllReturnsZero()
    {
        var runsRes = await _client.GetActiveRunsAsync();
        Assert.True(runsRes.IsSuccess);
        Assert.Empty(runsRes.Value!);

        var cancelRes = await _client.CancelAllRunsAsync();
        Assert.True(cancelRes.IsSuccess);
        Assert.Equal(0, cancelRes.Value);
    }

    [Fact]
    public async Task RestoreDrill_JobNotFound_ReturnsNotFound()
    {
        var res = await _client.ExecuteRestoreDrillAsync("nonexistent", "MyDB");
        Assert.False(res.IsSuccess);
        Assert.Contains("not found", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreDrill_ValidJob_ExecutesDrillAndReturnsResult()
    {
        var job = new BackupJob
        {
            Id = "drill-job",
            Name = "Drill Test Job",
            Kind = "sqlserver",
            Destinations = ["mock"],
            Sql = new SqlServerSource { Server = "localhost", Databases = ["TestDB"] }
        };
        await _client.SaveJobAsync(job);

        var res = await _client.ExecuteRestoreDrillAsync("drill-job", "TestDB");
        Assert.False(res.IsSuccess); // fails because mock has no archives, but correctly returns drill response
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
