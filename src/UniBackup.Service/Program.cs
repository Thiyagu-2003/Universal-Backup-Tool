using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UniBackup.Core.Interfaces;
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
using Serilog;

// 1. Handle command line service management flags
if (args.Length > 0)
{
    var flag = args[0].ToLowerInvariant();
    switch (flag)
    {
        case "--install":
        case "-i":
            var instResult = WindowsServiceManager.InstallService();
            Console.WriteLine(instResult.IsSuccess ? "Service installed successfully." : $"Error: {instResult.Error}");
            return instResult.IsSuccess ? 0 : 1;

        case "--uninstall":
        case "-u":
            var unResult = WindowsServiceManager.UninstallService();
            Console.WriteLine(unResult.IsSuccess ? "Service uninstalled successfully." : $"Error: {unResult.Error}");
            return unResult.IsSuccess ? 0 : 1;

        case "--start":
            var startResult = WindowsServiceManager.StartService();
            Console.WriteLine(startResult.IsSuccess ? "Service started successfully." : $"Error: {startResult.Error}");
            return startResult.IsSuccess ? 0 : 1;

        case "--stop":
            var stopResult = WindowsServiceManager.StopService();
            Console.WriteLine(stopResult.IsSuccess ? "Service stopped successfully." : $"Error: {stopResult.Error}");
            return stopResult.IsSuccess ? 0 : 1;

        case "--status":
            var status = WindowsServiceManager.GetServiceStatus();
            Console.WriteLine($"Service Status: {status}");
            return 0;
    }
}

// 2. Initialize Services and Host Builder for Windows Service
var crypto = new CredentialProtector();
var configService = new ConfigurationService(crypto);
var config = await configService.LoadConfigAsync();
LoggingSetup.Initialize(config.Backup.LogPath, config.Backup.MaxLogMb, config.Backup.KeepLogsDays);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = WindowsServiceManager.ServiceName;
});

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    ServiceApi.ConfigureNamedPipeKestrel(serverOptions);
});

// Singletons & Services
builder.Services.AddSingleton<ICredentialProtector>(crypto);
builder.Services.AddSingleton(configService);
builder.Services.AddSingleton<IArchiveService, HighSpeedZipArchiveService>();
builder.Services.AddSingleton<IStorageProvider, AwsS3StorageProvider>();
builder.Services.AddSingleton<IStorageProvider, GoogleDriveStorageProvider>();
builder.Services.AddSingleton<INotificationService, EmailNotificationService>();
builder.Services.AddSingleton<WebhookNotificationService>();
builder.Services.AddSingleton<SqlServerBackupService>();
builder.Services.AddSingleton<SqlServerRestoreService>();
builder.Services.AddSingleton<SqlRestoreOrchestrator>();
builder.Services.AddSingleton<RestoreDrillService>();
builder.Services.AddSingleton<BackupPipeline>();
builder.Services.AddSingleton<BatchBackupRunner>();
builder.Services.AddSingleton<JobExecutionCoordinator>();

// Hosted Worker Service
builder.Services.AddHostedService<UniBackupWorker>();

var app = builder.Build();
app.MapUniBackupEndpoints();
app.Run();
return 0;
