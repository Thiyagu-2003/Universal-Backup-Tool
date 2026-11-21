using UniBackup.Infrastructure.Compression;
using Xunit;

namespace UniBackup.Tests;

public class ZipCompressionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly HighSpeedZipArchiveService _zipService = new();

    public ZipCompressionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"CloudPacker_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task Test_Create_And_Extract_Zip()
    {
        // 1. Create dummy files
        var sourceDir = Path.Combine(_tempRoot, "Source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "file1.txt"), "Hello World 1");

        var subDir = Path.Combine(sourceDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file2.txt"), "Nested content");

        var zipPath = Path.Combine(_tempRoot, "test.zip");

        // 2. Compress
        var compressResult = await _zipService.CreateZipArchiveAsync(sourceDir, zipPath);
        Assert.True(compressResult.IsSuccess);
        Assert.True(File.Exists(zipPath));
        Assert.True(compressResult.Value > 0);

        // 3. Extract in overwrite mode
        var restoreDir = Path.Combine(_tempRoot, "Restore");
        var extractResult = await _zipService.ExtractZipArchiveAsync(zipPath, restoreDir, "overwrite");
        Assert.True(extractResult.IsSuccess);

        Assert.True(File.Exists(Path.Combine(restoreDir, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(restoreDir, "sub", "file2.txt")));
        Assert.Equal("Hello World 1", File.ReadAllText(Path.Combine(restoreDir, "file1.txt")));
        Assert.Equal("Nested content", File.ReadAllText(Path.Combine(restoreDir, "sub", "file2.txt")));
    }
}
