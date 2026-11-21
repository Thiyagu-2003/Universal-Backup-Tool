using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Serilog;
using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;

namespace UniBackup.Infrastructure.Storage;

public class WebDavStorageProvider : IStorageProvider
{
    public string Name => "webdav";

    public bool IsEnabled(AppConfig config) => config.WebDav.Enabled;

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

        if (!string.IsNullOrWhiteSpace(config.WebDav.Username))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.WebDav.Username}:{config.WebDav.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        return client;
    }

    private string BuildUrl(AppConfig config, string path)
    {
        var baseUri = config.WebDav.ServerUrl.TrimEnd('/');
        var cleanPath = path.TrimStart('/');
        return $"{baseUri}/{cleanPath}";
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

        var fileName = Path.GetFileName(localFilePath);
        var folder = targetFolderOverride ?? DestinationFolders.Resolve(Name, config, job);
        var remotePath = $"{folder.TrimEnd('/')}/{fileName}";
        var url = BuildUrl(config, remotePath);

        var sw = Stopwatch.StartNew();
        using var client = CreateClient(config);

        try
        {
            var fi = new FileInfo(localFilePath);
            var totalBytes = fi.Length;

            await using var fs = File.OpenRead(localFilePath);
            using var content = new StreamContent(fs, 81920);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = await client.PutAsync(url, content, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Created)
            {
                result.Status = "SUCCESS";
                result.BytesUploaded = totalBytes;
                result.DurationSeconds = sw.Elapsed.TotalSeconds;
                result.TargetLocation = url;

                Log.Information("[WebDAV] Uploaded {File} ({Bytes:N0} bytes) to {Url} in {Duration:F1}s",
                    fileName, totalBytes, url, sw.Elapsed.TotalSeconds);

                return Result<DestinationRunResult>.Success(result);
            }

            return Result<DestinationRunResult>.Failure($"WebDAV upload failed with status: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationSeconds = sw.Elapsed.TotalSeconds;
            result.ErrorMessage = ex.Message;
            Log.Error(ex, "[WebDAV] Failed uploading {File} to {Url}", fileName, url);
            return Result<DestinationRunResult>.Failure($"WebDAV upload failed: {ex.Message}");
        }
    }

    public async Task<Result<List<CloudArchiveItem>>> ListArchivesAsync(
        AppConfig config,
        string? folderPrefix = null,
        CancellationToken ct = default)
    {
        var items = new List<CloudArchiveItem>();
        var folder = folderPrefix ?? config.WebDav.RemoteFolder;
        var url = BuildUrl(config, folder);

        using var client = CreateClient(config);
        try
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
            req.Headers.Add("Depth", "1");
            var res = await client.SendAsync(req, ct);

            if (!res.IsSuccessStatusCode)
            {
                return Result<List<CloudArchiveItem>>.Success(items);
            }

            var xml = await res.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            XNamespace d = "DAV:";

            foreach (var resp in doc.Descendants(d + "response"))
            {
                var href = resp.Element(d + "href")?.Value ?? "";
                var propstat = resp.Element(d + "propstat");
                var prop = propstat?.Element(d + "prop");

                var isCollection = prop?.Element(d + "resourcetype")?.Element(d + "collection") != null;
                if (isCollection) continue;

                var contentLength = (long?)prop?.Element(d + "getcontentlength") ?? 0;
                var lastModifiedStr = (string?)prop?.Element(d + "getlastmodified");
                DateTime lastMod = DateTime.TryParse(lastModifiedStr, out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;

                var displayName = Path.GetFileName(Uri.UnescapeDataString(href).TrimEnd('/'));
                if (string.IsNullOrEmpty(displayName)) continue;

                items.Add(new CloudArchiveItem
                {
                    KeyOrId = href,
                    DisplayName = displayName,
                    CloudFolder = folder,
                    LastModified = lastMod,
                    SizeBytes = contentLength,
                    Destination = Name
                });
            }

            return Result<List<CloudArchiveItem>>.Success(items.OrderByDescending(i => i.LastModified).ToList());
        }
        catch (Exception ex)
        {
            return Result<List<CloudArchiveItem>>.Failure($"WebDAV list failed: {ex.Message}");
        }
    }

    public async Task<Result> DownloadAsync(
        AppConfig config,
        string keyOrId,
        string destinationFilePath,
        IProgress<(long downloadedBytes, long totalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            var url = keyOrId.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? keyOrId : BuildUrl(config, keyOrId);
            var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return Result.Failure($"WebDAV download failed: {res.StatusCode}");

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destinationFilePath);
            await src.CopyToAsync(dst, ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"WebDAV download failed: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(AppConfig config, string keyOrId, CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            var url = keyOrId.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? keyOrId : BuildUrl(config, keyOrId);
            var res = await client.DeleteAsync(url, ct);
            return res.IsSuccessStatusCode ? Result.Success() : Result.Failure($"WebDAV delete failed: {res.StatusCode}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"WebDAV delete failed: {ex.Message}");
        }
    }

    public Task<Result<int>> CleanupRetentionAsync(
        AppConfig config,
        string targetFolder,
        int retentionDays,
        CancellationToken ct = default)
    {
        return Task.FromResult(Result<int>.Success(0));
    }

    public async Task<Result> ValidateConnectionAsync(AppConfig config, CancellationToken ct = default)
    {
        using var client = CreateClient(config);
        try
        {
            var url = BuildUrl(config, config.WebDav.RemoteFolder);
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
            req.Headers.Add("Depth", "0");
            var res = await client.SendAsync(req, ct);
            return (res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.NotFound)
                ? Result.Success()
                : Result.Failure($"WebDAV connection returned: {res.StatusCode}");
        }
        catch (Exception ex)
        {
            return Result.Failure($"WebDAV connection test failed: {ex.Message}");
        }
    }
}
