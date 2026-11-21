using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text.Json;
using UniBackup.Core.Common;
using UniBackup.Core.Models;

namespace UniBackup.Core.Client;

public class UniBackupClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UniBackupClient(string pipeName = "UniBackup")
    {
        _ownsClient = true;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await stream.ConnectAsync(cancellationToken);
                return stream;
            }
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/"),
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public UniBackupClient(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = false;
        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri("http://localhost/");
        }
    }

    public async Task<bool> IsServiceAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));
            var health = await GetHealthAsync(cts.Token);
            return health.IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result<ServiceHealth>> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<ServiceHealth>("/health", ct);
            return res != null ? Result<ServiceHealth>.Success(res) : Result<ServiceHealth>.Failure("Empty response from service.");
        }
        catch (Exception ex)
        {
            return Result<ServiceHealth>.Failure("Service unreachable: " + ex.Message);
        }
    }

    public async Task<Result<List<BackupJob>>> GetJobsAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<List<BackupJob>>("/jobs", ct);
            return Result<List<BackupJob>>.Success(res ?? []);
        }
        catch (Exception ex)
        {
            return Result<List<BackupJob>>.Failure(ex.Message);
        }
    }

    public async Task<Result<BackupJob>> GetJobAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<BackupJob>($"/jobs/{id}", ct);
            return res != null ? Result<BackupJob>.Success(res) : Result<BackupJob>.Failure($"Job '{id}' not found.");
        }
        catch (Exception ex)
        {
            return Result<BackupJob>.Failure(ex.Message);
        }
    }

    public async Task<Result> SaveJobAsync(BackupJob job, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsJsonAsync("/jobs", job, ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure($"Save failed ({res.StatusCode})");
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result> DeleteJobAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.DeleteAsync($"/jobs/{id}", ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure($"Delete failed ({res.StatusCode})");
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result<string>> RunJobAsync(string id, SqlBackupType? type = null, CancellationToken ct = default)
    {
        try
        {
            var url = type.HasValue ? $"/jobs/{id}/run?type={type.Value}" : $"/jobs/{id}/run";
            var res = await _http.PostAsync(url, null, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                return Result<string>.Failure($"Run failed ({res.StatusCode}): {err}");
            }
            var runId = await res.Content.ReadAsStringAsync(ct);
            return Result<string>.Success(runId.Trim('"', ' '));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(ex.Message);
        }
    }

    public async Task<Result> CancelRunAsync(string runId, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsync($"/runs/{runId}/cancel", null, ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure("Cancel failed.");
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result<int>> CancelAllRunsAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsync("/runs/cancel-all", null, ct);
            if (res.IsSuccessStatusCode)
            {
                var count = await res.Content.ReadFromJsonAsync<int>(ct);
                return Result<int>.Success(count);
            }
            return Result<int>.Failure("Cancel all failed.");
        }
        catch (Exception ex)
        {
            return Result<int>.Failure(ex.Message);
        }
    }

    public async Task<Result<List<ActiveRunInfo>>> GetActiveRunsAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<List<ActiveRunInfo>>("/runs/active", ct);
            return Result<List<ActiveRunInfo>>.Success(res ?? []);
        }
        catch (Exception ex)
        {
            return Result<List<ActiveRunInfo>>.Failure(ex.Message);
        }
    }

    public async Task<Result<List<RunHistoryRecord>>> GetHistoryAsync(string? jobId = null, int limit = 100, CancellationToken ct = default)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(jobId) ? $"/history?limit={limit}" : $"/history?jobId={jobId}&limit={limit}";
            var res = await _http.GetFromJsonAsync<List<RunHistoryRecord>>(url, ct);
            return Result<List<RunHistoryRecord>>.Success(res ?? []);
        }
        catch (Exception ex)
        {
            return Result<List<RunHistoryRecord>>.Failure(ex.Message);
        }
    }

    public async Task<Result<AppConfig>> GetSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<AppConfig>("/settings", ct);
            return res != null ? Result<AppConfig>.Success(res) : Result<AppConfig>.Failure("Failed to load settings.");
        }
        catch (Exception ex)
        {
            return Result<AppConfig>.Failure(ex.Message);
        }
    }

    public async Task<Result> SaveSettingsAsync(AppConfig config, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PutAsJsonAsync("/settings", config, ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure("Failed to save settings.");
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result> TestDestinationAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsync($"/destinations/{name}/test", null, ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result> TestSqlConnectionAsync(SqlServerSource source, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsJsonAsync("/sql/test-connection", source, ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result<List<SqlDatabaseInfo>>> ListSqlDatabasesAsync(SqlServerSource source, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.PostAsJsonAsync("/sql/databases", source, ct);
            if (res.IsSuccessStatusCode)
            {
                var list = await res.Content.ReadFromJsonAsync<List<SqlDatabaseInfo>>(ct);
                return Result<List<SqlDatabaseInfo>>.Success(list ?? []);
            }
            return Result<List<SqlDatabaseInfo>>.Failure(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return Result<List<SqlDatabaseInfo>>.Failure(ex.Message);
        }
    }

    public async Task SubscribeEventsAsync(Func<ProgressEvent, Task> onEvent, CancellationToken ct)
    {
        using var response = await _http.GetAsync("/events", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.StartsWith("data: "))
            {
                var json = line[6..];
                try
                {
                    var ev = JsonSerializer.Deserialize<ProgressEvent>(json);
                    if (ev != null)
                    {
                        await onEvent(ev);
                    }
                }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
