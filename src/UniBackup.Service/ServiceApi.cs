using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Engine.Pipeline;
using UniBackup.Engine.Services;
using UniBackup.Infrastructure.Sql;

namespace UniBackup.Service;

public static class ServiceApi
{
    public const string NamedPipeName = "UniBackup";

    public static void ConfigureNamedPipeKestrel(KestrelServerOptions options, string pipeName = NamedPipeName)
    {
        options.ListenNamedPipe(pipeName);
    }

    public static WebApplication MapUniBackupEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new ServiceHealth("Healthy", "UniBackup Pro", "2.0", DateTime.UtcNow)));

        // Jobs CRUD
        app.MapGet("/jobs", async (ConfigurationService cs) => Results.Ok(await cs.LoadJobsAsync()));

        app.MapGet("/jobs/{id}", async (string id, ConfigurationService cs) =>
        {
            var jobs = await cs.LoadJobsAsync();
            var job = jobs.FirstOrDefault(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return job != null ? Results.Ok(job) : Results.NotFound();
        });

        app.MapPost("/jobs", async (BackupJob job, ConfigurationService cs) =>
        {
            await cs.SaveJobAsync(job);
            return Results.Ok();
        });

        app.MapDelete("/jobs/{id}", async (string id, ConfigurationService cs) =>
        {
            var deleted = await cs.DeleteJobAsync(id);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        // Run execution & cancellation
        app.MapPost("/jobs/{id}/run", async (string id, string? type, JobExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            SqlBackupType? sqlType = null;
            if (!string.IsNullOrEmpty(type) && Enum.TryParse<SqlBackupType>(type, true, out var parsed))
            {
                sqlType = parsed;
            }

            var res = await coordinator.TriggerRunAsync(id, sqlType, ct);
            return res.IsSuccess ? Results.Ok(res.Value) : Results.BadRequest(res.Error);
        });

        app.MapPost("/runs/{runId}/cancel", (string runId, JobExecutionCoordinator coordinator) =>
        {
            var cancelled = coordinator.CancelRun(runId);
            return cancelled ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/runs/cancel-all", (JobExecutionCoordinator coordinator) =>
        {
            var count = coordinator.CancelAll();
            return Results.Ok(count);
        });

        app.MapGet("/runs/active", (JobExecutionCoordinator coordinator) =>
            Results.Ok(coordinator.GetActiveRuns()));

        // History
        app.MapGet("/history", async (string? jobId, int? limit, ConfigurationService cs) =>
        {
            var max = limit ?? 100;
            var history = await cs.HistoryRepository.GetRunsAsync(max, jobId);
            return Results.Ok(history);
        });

        // Settings
        app.MapGet("/settings", async (ConfigurationService cs) => Results.Ok(await cs.LoadConfigAsync()));

        app.MapPut("/settings", async (AppConfig cfg, ConfigurationService cs) =>
        {
            await cs.SaveConfigAsync(cfg);
            return Results.Ok();
        });

        // Diagnostics / destination test
        app.MapPost("/destinations/{name}/test", async (string name, IEnumerable<IStorageProvider> providers, ConfigurationService cs, CancellationToken ct) =>
        {
            var provider = providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (provider == null) return Results.NotFound($"Destination '{name}' not supported.");

            var config = await cs.LoadConfigAsync();
            var res = await provider.ValidateConnectionAsync(config, ct);
            return res.IsSuccess ? Results.Ok("Connection valid") : Results.BadRequest(res.Error);
        });

        app.MapPost("/sql/test-connection", async (SqlServerSource source, SqlServerBackupService sql, CancellationToken ct) =>
        {
            var res = await sql.ListDatabasesAsync(source, ct);
            return res.IsSuccess ? Results.Ok("Connection successful") : Results.BadRequest(res.Error);
        });

        app.MapPost("/sql/databases", async (SqlServerSource source, SqlServerBackupService sql, CancellationToken ct) =>
        {
            var res = await sql.ListDatabasesAsync(source, ct);
            return res.IsSuccess ? Results.Ok(res.Value) : Results.BadRequest(res.Error);
        });

        // Server-Sent Events (SSE) Progress Stream
        app.MapGet("/events", async (HttpContext ctx, JobExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            ctx.Response.Headers.Append("Content-Type", "text/event-stream");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            ctx.Response.Headers.Append("Connection", "keep-alive");

            var reader = coordinator.EventReader;
            while (!ct.IsCancellationRequested)
            {
                if (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var ev))
                    {
                        var json = JsonSerializer.Serialize(ev);
                        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            }
        });

        return app;
    }
}
