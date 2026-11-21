using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Notifications;

public class HeartbeatService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<Result> PingAsync(HeartbeatSettings settings, bool success, CancellationToken ct = default)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url))
        {
            return Result.Success();
        }

        var url = settings.Url.Trim();
        if (!success)
        {
            if (!string.IsNullOrWhiteSpace(settings.FailUrl))
            {
                url = settings.FailUrl.Trim();
            }
            else if (settings.AppendFailPath)
            {
                url = url.TrimEnd('/') + "/fail";
            }
        }

        try
        {
            Log.Information("[Heartbeat] Sending ping to {Url} (Success: {Status})", url, success);
            var response = await HttpClient.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            Log.Warning("[Heartbeat] Ping to {Url} returned non-success code: {Code}", url, response.StatusCode);
            return Result.Failure($"Heartbeat ping failed: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Heartbeat] Could not reach heartbeat monitor {Url}", url);
            return Result.Failure($"Heartbeat error: {ex.Message}");
        }
    }
}
