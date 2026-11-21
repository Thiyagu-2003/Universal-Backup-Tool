using UniBackup.Core.Interfaces;
using UniBackup.Core.Models;
using UniBackup.Infrastructure.Storage;
using Xunit;

namespace UniBackup.Tests;

public class LocalStorageProviderTests : StorageProviderContractTests
{
    private readonly string _storageTargetDir;

    public LocalStorageProviderTests()
    {
        _storageTargetDir = Path.Combine(TestDir, "TargetStorage");
        Directory.CreateDirectory(_storageTargetDir);
    }

    protected override IStorageProvider CreateProvider() => new LocalStorageProvider();

    protected override AppConfig CreateConfig() => new()
    {
        Local = new LocalStorageSettings
        {
            Enabled = true,
            TargetPath = _storageTargetDir,
            RetentionDays = 30
        }
    };

    protected override bool IsConfigured() => true;

    [Fact]
    public async Task ValidateConnection_WithValidDirectory_ReturnsSuccess()
    {
        var provider = new LocalStorageProvider();
        var config = CreateConfig();

        var res = await provider.ValidateConnectionAsync(config);
        Assert.True(res.IsSuccess);
    }

    [Fact]
    public async Task CleanupRetention_DeletesFilesOlderThanRetentionDays()
    {
        var provider = new LocalStorageProvider();
        var config = CreateConfig();

        // Create an old file
        var oldFile = Path.Combine(_storageTargetDir, "OldArchive_20200101_000000.zip");
        await File.WriteAllTextAsync(oldFile, "old archive");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-40));

        // Create a new file
        var newFile = Path.Combine(_storageTargetDir, "NewArchive_20261121_000000.zip");
        await File.WriteAllTextAsync(newFile, "new archive");

        var cleanupRes = await provider.CleanupRetentionAsync(config, _storageTargetDir, 30);
        Assert.True(cleanupRes.IsSuccess);
        Assert.Equal(1, cleanupRes.Value);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }
}
