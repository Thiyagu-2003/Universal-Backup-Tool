using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class OneDriveStorageProvider : IStorageProvider
{
    public string Name => "onedrive";

    public bool IsEnabled(AppConfig config) => config.OneDrive.Enabled;

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

        if (!string.IsNullOrWhiteSpace(config.OneDrive.AccessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.OneDrive.AccessToken.Trim());
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

        if (string.IsNullOrWhiteSpace(config.OneDrive.AccessToken))
            return Result<DestinationRunResult>.Failure("OneDrive Access Token is not configured.");

        var fileName = Path.GetFileName(localFilePath);
        var folder = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
        var cleanFolder = folder.Trim('/');
        var remotePath = string.IsNullOrWhiteSpace(cleanFolder) ? $"/{fileName}" : $"/{cleanFolder}/{fileName}";

        var sw = Stopwatch.StartNew();
        using var client = CreateClient(config);

        try
        {
            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            var url = $"https://graph.microsoft.com/v1.0/me/drive/root:{remotePath}:/content";
            await using var fs = File.OpenRead(localFilePath);
            using var content = new StreamContent(fs, 81920);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = await client.PutAsync(url, content, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Created)
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
        if (string.IsNullOrWhiteSpace(config.OneDrive.AccessToken))
            return Result<List<CloudArchiveItem>>.Failure("OneDrive Access Token is not configured.");

        using var client = CreateClient(config);
        var folder = folderPrefix ?? config.OneDrive.RemoteFolder;
        var cleanFolder = folder.Trim('/');

        var url = string.IsNullOrWhiteSpace(cleanFolder)
            ? "https://graph.microsoft.com/v1.0/me/drive/root/children"
            : $"https://graph.microsoft.com/v1.0/me/drive/root:/{cleanFolder}:/children";

        try
        {
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result<List<CloudArchiveItem>>.Failure($"OneDrive list failed: {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonNode.Parse(json);
            var values = doc?["value"]?.AsArray();
            var items = new List<CloudArchiveItem>();

            if (values != null)
            {
                foreach (var item in values)
                {
                    if (item?["file"] != null)
                    {
                        var name = item["name"]?.GetValue<string>() ?? "";
                        var id = item["id"]?.GetValue<string>() ?? name;
                        var size = item["size"]?.GetValue<long>() ?? 0;
                        var modifiedStr = item["lastModifiedDateTime"]?.GetValue<string>();
                        DateTime? modified = DateTime.TryParse(modifiedStr, out var dt) ? dt : null;

                        items.Add(new CloudArchiveItem
                        {
                            KeyOrId = id,
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
            return Result<List<CloudArchiveItem>>.Failure($"OneDrive List failed: {ex.Message}");
        }
    }

    public async Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.OneDrive.AccessToken))
            return Result.Failure("OneDrive Access Token is not configured.");

        using var client = CreateClient(config);
        try
        {
            var url = keyOrId.StartsWith("/")
                ? $"https://graph.microsoft.com/v1.0/me/drive/root:{keyOrId}:/content"
                : $"https://graph.microsoft.com/v1.0/me/drive/items/{keyOrId}/content";

            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"OneDrive download failed: HTTP {(int)response.StatusCode} {err}");
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
            return Result.Failure($"OneDrive download error: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(
        AppConfig config,
        string keyOrId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.OneDrive.AccessToken))
            return Result.Failure("OneDrive Access Token is not configured.");

        using var client = CreateClient(config);
        try
        {
            var url = keyOrId.StartsWith("/")
                ? $"https://graph.microsoft.com/v1.0/me/drive/root:{keyOrId}"
                : $"https://graph.microsoft.com/v1.0/me/drive/items/{keyOrId}";

            var response = await client.DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Result.Success();

            var err = await response.Content.ReadAsStringAsync(ct);
            return Result.Failure($"OneDrive delete failed: {err}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"OneDrive delete error: {ex.Message}");
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
        if (!listRes.IsSuccess) return Result<int>.Failure(listRes.Error ?? "OneDrive list failed");

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
                    Log.Information("Retention pruned old OneDrive backup: {Name}", item.DisplayName);
                }
            }
        }

        return Result<int>.Success(deletedCount);
    }

    public async Task<Result> ValidateConnectionAsync(
        AppConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.OneDrive.AccessToken))
            return Result.Failure("OneDrive Access Token is empty.");

        using var client = CreateClient(config);
        try
        {
            var response = await client.GetAsync("https://graph.microsoft.com/v1.0/me/drive", ct);
            if (response.IsSuccessStatusCode)
                return Result.Success();

            var err = await response.Content.ReadAsStringAsync(ct);
            return Result.Failure($"OneDrive authentication failed: HTTP {(int)response.StatusCode} {err}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"OneDrive connection error: {ex.Message}");
        }
    }
}
