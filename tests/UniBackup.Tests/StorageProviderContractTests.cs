using UniBackup.Core.Common;
using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public abstract class StorageProviderContractTests : IDisposable
{
    protected abstract IStorageProvider CreateProvider();
    protected abstract AppConfig CreateConfig();
    protected abstract bool IsConfigured();

    protected string TestDir { get; } = Path.Combine(Path.GetTempPath(), $"UniBackup_StorageContract_{Guid.NewGuid():N}");

    public StorageProviderContractTests()
    {
        Directory.CreateDirectory(TestDir);
    }

    [Fact]
    public async Task Contract_Upload_List_Download_Delete_RoundTrip()
    {
        if (!IsConfigured()) return; // Gated by environment variable / configuration

        var provider = CreateProvider();
        var config = CreateConfig();
        var job = new BackupJob { Id = "contract-job", Name = "Contract Job" };

        var localFile = Path.Combine(TestDir, "test_backup.zip");
        await File.WriteAllTextAsync(localFile, "Test archive payload for storage contract test");

        // 1. Upload
        var uploadRes = await provider.UploadAsync(config, job, localFile);
        Assert.True(uploadRes.IsSuccess);
        Assert.Equal("SUCCESS", uploadRes.Value!.Status);
        Assert.True(uploadRes.Value.BytesUploaded > 0);

        // 2. List
        var listRes = await provider.ListArchivesAsync(config);
        Assert.True(listRes.IsSuccess);
        Assert.Contains(listRes.Value!, a => a.DisplayName == "test_backup.zip");

        var uploadedItem = listRes.Value!.First(a => a.DisplayName == "test_backup.zip");

        // 3. Download
        var downloadedFile = Path.Combine(TestDir, "downloaded_test_backup.zip");
        var dlRes = await provider.DownloadAsync(config, uploadedItem.KeyOrId, downloadedFile);
        Assert.True(dlRes.IsSuccess);
        Assert.True(File.Exists(downloadedFile));
        Assert.Equal(File.ReadAllText(localFile), File.ReadAllText(downloadedFile));

        // 4. Delete
        var delRes = await provider.DeleteAsync(config, uploadedItem.KeyOrId);
        Assert.True(delRes.IsSuccess);
    }

    [Fact]
    public async Task Contract_ZeroByteFile_HandlesCleanly()
    {
        if (!IsConfigured()) return;

        var provider = CreateProvider();
        var config = CreateConfig();
        var job = new BackupJob { Id = "contract-zero", Name = "Zero Byte Job" };

        var zeroFile = Path.Combine(TestDir, "empty.zip");
        await File.WriteAllBytesAsync(zeroFile, []);

        var uploadRes = await provider.UploadAsync(config, job, zeroFile);
        Assert.True(uploadRes.IsSuccess);

        var listRes = await provider.ListArchivesAsync(config);
        Assert.True(listRes.IsSuccess);
        var item = listRes.Value!.FirstOrDefault(a => a.DisplayName == "empty.zip");
        if (item != null)
        {
            await provider.DeleteAsync(config, item.KeyOrId);
        }
    }

    public virtual void Dispose()
    {
        try { Directory.Delete(TestDir, true); } catch { }
    }
}
