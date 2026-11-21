using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class DropboxStorageProvider : IStorageProvider
{
    public string Name => "dropbox";

    public bool IsEnabled(AppConfig config) => config.Dropbox.Enabled;

    private HttpClient CreateClient(AppConfig config)
    {
        var handler = new HttpClientHandler();
        if (config.Proxy.Enabled && !string.IsNullOrWhiteSpace(config.Proxy.ProxyUrl))
        {
            handler.Proxy = new System.Net.WebProxy(config.Proxy.ProxyUrl);
            if (!string.IsNullOrWhiteSpace(config.Proxy.Username))
            {
                handler.Proxy.Credentials = new System.Net.NetworkCredential(config.Proxy.Username, config.Proxy.Password);
            }
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        if (!string.IsNullOrWhiteSpace(config.Dropbox.AccessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Dropbox.AccessToken.Trim());
        }

        return client;
    }

    public async Task<Result<DestinationRunResult>> UploadAsync(
        AppConfig config,
        BackupJob job,
        string localFilePath,
        string? targetFolderOverride = null,
        IProgress<(long uploadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new DestinationRunResult
        {
            Destination = Name,
            Status = "FAILED"
        };

        if (!File.Exists(localFilePath))
            return Result<DestinationRunResult>.Failure($"Archive file does not exist: {localFilePath}");

        if (string.IsNullOrWhiteSpace(config.Dropbox.AccessToken))
            return Result<DestinationRunResult>.Failure("Dropbox Access Token is not configured.");

        var fileName = Path.GetFileName(localFilePath);
        var folder = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
        var remotePath = $"/{folder.Trim('/')}/{fileName}";

        var sw = Stopwatch.StartNew();
        using var client = CreateClient(config);

        try
        {
            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload");
            var argJson = JsonSerializer.Serialize(new
            {
                path = remotePath,
                mode = "overwrite",
                autorename = false,
                mute = false
            });
            req.Headers.Add("Dropbox-API-Arg", argJson);

            await using var fs = File.OpenRead(localFilePath);
            req.Content = new StreamContent(fs, 81920);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = await client.SendAsync(req, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonNode.Parse(respBody);
                var remoteId = doc?["id"]?.GetValue<string>() ?? remotePath;

                result.Status = "SUCCESS";
                result.TargetLocation = remoteId;
                result.DurationSeconds = sw.Elapsed.TotalSeconds;
                result.BytesUploaded = totalBytes;

                progress?.Report((totalBytes, totalBytes));
                return Result<DestinationRunResult>.Success(result);
            }

            var err = await response.Content.ReadAsStringAsync(ct);
            result.ErrorMessage = $"HTTP {(int)response.StatusCode}: {err}";
            return Result<DestinationRunResult>.Failure(result.ErrorMessage);
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.ErrorMessage = ex.Message;
            return Result<DestinationRunResult>.Failure(ex.Message);
        }
    }

    public async Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Dropbox.AccessToken))
            return Result<List<CloudArchiveItem>>.Failure("Dropbox Access Token is not configured.");

        using var client = CreateClient(config);
        var folder = folderPrefix ?? config.Dropbox.RemoteFolder;
        if (string.IsNullOrWhiteSpace(folder) || folder == "/") folder = "";
        else if (!folder.StartsWith("/")) folder = "/" + folder;

        try
        {
            var reqObj = new { path = folder, recursive = false };
            var content = new StringContent(JsonSerializer.Serialize(reqObj), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.dropboxapi.com/2/files/list_folder", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result<List<CloudArchiveItem>>.Failure($"Dropbox list_folder failed: {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonNode.Parse(json);
            var entries = doc?["entries"]?.AsArray();
            var items = new List<CloudArchiveItem>();

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    var tag = entry != null ? sourceTag(entry) : "";
                    if (tag == "file")
                    {
                        var name = entry?["name"]?.GetValue<string>() ?? "";
                        var path = entry?["path_display"]?.GetValue<string>() ?? name;
                        var size = entry?["size"]?.GetValue<long>() ?? 0;
                        var modifiedStr = entry?["server_modified"]?.GetValue<string>();
                        DateTime? modified = DateTime.TryParse(modifiedStr, out var dt) ? dt : null;

                        items.Add(new CloudArchiveItem
                        {
                            KeyOrId = path,
                            DisplayName = name,
                            CloudFolder = folder,
                            SizeBytes = size,
                            LastModified = modified,
                            Destination = Name
                        });
                    }
                }
            }

            return Result<List<CloudArchiveItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return Result<List<CloudArchiveItem>>.Failure($"Dropbox List failed: {ex.Message}");
        }

        static string sourceTag(JsonNode? node) => node?["tag"]?.GetValue<string>() ?? node?[".tag"]?.GetValue<string>() ?? "";
    }

    public async Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Dropbox.AccessToken))
            return Result.Failure("Dropbox Access Token is not configured.");

        using var client = CreateClient(config);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
            var argJson = JsonSerializer.Serialize(new { path = keyOrId.StartsWith("/") ? keyOrId : "/" + keyOrId });
            req.Headers.Add("Dropbox-API-Arg", argJson);

            var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"Dropbox download failed: HTTP {(int)response.StatusCode} {err}");
            }

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var inStream = await response.Content.ReadAsStreamAsync(ct);
            await using var outStream = File.Create(destinationFilePath);
            await inStream.CopyToAsync(outStream, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Dropbox download exception: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(
        AppConfig config,
        string keyOrId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Dropbox.AccessToken))
            return Result.Failure("Dropbox Access Token is not configured.");

        using var client = CreateClient(config);
        try
        {
            var reqObj = new { path = keyOrId.StartsWith("/") ? keyOrId : "/" + keyOrId };
            var content = new StringContent(JsonSerializer.Serialize(reqObj), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.dropboxapi.com/2/files/delete_v2", content, ct);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Result.Success();
            }

            var err = await response.Content.ReadAsStringAsync(ct);
            return Result.Failure($"Dropbox delete failed: {err}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Dropbox delete error: {ex.Message}");
        }
    }

    public async Task<Result<int>> CleanupRetentionAsync(
        AppConfig config,
        string targetFolder,
        int retentionDays,
        CancellationToken ct = default)
    {
        if (retentionDays <= 0) return Result<int>.Success(0);

        var listRes = await ListArchivesAsync(config, targetFolder, ct);
        if (!listRes.IsSuccess) return Result<int>.Failure(listRes.Error ?? "Dropbox list failed");

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deletedCount = 0;

        foreach (var item in listRes.Value)
        {
            if (item.LastModified.HasValue && item.LastModified.Value < cutoff)
            {
                var delRes = await DeleteAsync(config, item.KeyOrId, ct);
                if (delRes.IsSuccess)
                {
                    deletedCount++;
                    Log.Information("Retention pruned old Dropbox backup: {Path}", item.KeyOrId);
                }
            }
        }

        return Result<int>.Success(deletedCount);
    }

    public async Task<Result> ValidateConnectionAsync(
        AppConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Dropbox.AccessToken))
            return Result.Failure("Dropbox Access Token is empty.");

        using var client = CreateClient(config);
        try
        {
            var response = await client.PostAsync("https://api.dropboxapi.com/2/users/get_current_account", null, ct);
            if (response.IsSuccessStatusCode)
                return Result.Success();

            var err = await response.Content.ReadAsStringAsync(ct);
            return Result.Failure($"Dropbox authentication failed: HTTP {(int)response.StatusCode} {err}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Dropbox connection test error: {ex.Message}");
        }
    }
}
