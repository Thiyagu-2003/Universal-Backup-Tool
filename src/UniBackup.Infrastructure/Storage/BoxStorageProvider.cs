using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class BoxStorageProvider : IStorageProvider
{
    public string Name => "box";

    public bool IsEnabled(AppConfig config) => config.Box.Enabled;

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

        if (!string.IsNullOrWhiteSpace(config.Box.AccessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Box.AccessToken.Trim());
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

        if (string.IsNullOrWhiteSpace(config.Box.AccessToken))
            return Result<DestinationRunResult>.Failure("Box Access Token is not configured.");

        var fileName = Path.GetFileName(localFilePath);
        var folderId = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
        if (string.IsNullOrWhiteSpace(folderId)) folderId = "0";

        var sw = Stopwatch.StartNew();
        using var client = CreateClient(config);

        try
        {
            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            using var form = new MultipartFormDataContent();
            var attributesJson = JsonSerializer.Serialize(new
            {
                name = fileName,
                parent = new { id = folderId }
            });

            form.Add(new StringContent(attributesJson), "attributes");
            await using var fs = File.OpenRead(localFilePath);
            var fileContent = new StreamContent(fs, 81920);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", fileName);

            var response = await client.PostAsync("https://upload.box.com/api/2.0/files/content", form, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Created)
            {
                var respBody = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonNode.Parse(respBody);
                var entries = doc?["entries"]?.AsArray();
                var fileId = entries?.FirstOrDefault()?["id"]?.GetValue<string>() ?? fileName;

                result.Status = "SUCCESS";
                result.TargetLocation = fileId;
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
        if (string.IsNullOrWhiteSpace(config.Box.AccessToken))
            return Result<List<CloudArchiveItem>>.Failure("Box Access Token is not configured.");

        using var client = CreateClient(config);
        var folderId = folderPrefix ?? config.Box.FolderId;
        if (string.IsNullOrWhiteSpace(folderId)) folderId = "0";

        var url = $"https://api.box.com/2.0/folders/{folderId}/items?fields=id,name,size,modified_at,type";

        try
        {
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result<List<CloudArchiveItem>>.Failure($"Box list failed: {err}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonNode.Parse(json);
            var entries = doc?["entries"]?.AsArray();
            var items = new List<CloudArchiveItem>();

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    var type = entry?["type"]?.GetValue<string>();
                    if (type == "file")
                    {
                        var name = entry?["name"]?.GetValue<string>() ?? "";
                        var id = entry?["id"]?.GetValue<string>() ?? name;
                        var size = entry?["size"]?.GetValue<long>() ?? 0;
                        var modifiedStr = entry?["modified_at"]?.GetValue<string>();
                        DateTime? modified = DateTime.TryParse(modifiedStr, out var dt) ? dt : null;

                        items.Add(new CloudArchiveItem
                        {
                            KeyOrId = id,
                            DisplayName = name,
                            CloudFolder = folderId,
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
            return Result<List<CloudArchiveItem>>.Failure($"Box List failed: {ex.Message}");
        }
    }

    public async Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Box.AccessToken))
            return Result.Failure("Box Access Token is not configured.");

        using var client = CreateClient(config);
        try
        {
            var url = $"https://api.box.com/2.0/files/{keyOrId}/content";
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                return Result.Failure($"Box download failed: HTTP {(int)response.StatusCode} {err}");
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
            return Result.Failure($"Box download error: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(
        AppConfig config,
        string keyOrId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Box.AccessToken))
            return Result.Failure("Box Access Token is not configured.");

        using var client = CreateClient(config);
        try
        {
            var url = $"https://api.box.com/2.0/files/{keyOrId}";
            var response = await client.DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Result.Success();

            var err = await response.Content.ReadAsStringAsync(ct);
            return Result.Failure($"Box delete failed: {err}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Box delete error: {ex.Message}");
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
        if (!listRes.IsSuccess) return Result<int>.Failure(listRes.Error ?? "Box list failed");

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
                    Log.Information("Retention pruned old Box backup: {Name}", item.DisplayName);
                }
            }
        }

        return Result<int>.Success(deletedCount);
    }

    public async Task<Result> ValidateConnectionAsync(
        AppConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Box.AccessToken))
            return Result.Failure("Box Access Token is empty.");

        using var client = CreateClient(config);
        try
        {
            var response = await client.GetAsync("https://api.box.com/2.0/users/me", ct);
            if (response.IsSuccessStatusCode)
                return Result.Success();

            var err = await response.Content.ReadAsStringAsync(ct);
            return Result.Failure($"Box authentication failed: HTTP {(int)response.StatusCode} {err}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Box connection error: {ex.Message}");
        }
    }
}
