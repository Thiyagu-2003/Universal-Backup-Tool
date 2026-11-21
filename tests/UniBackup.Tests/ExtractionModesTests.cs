using UniBackup.Infrastructure.Compression;
using Xunit;

namespace UniBackup.Tests;

public class ExtractionModesTests : IDisposable
{
    private readonly string _testRoot;
    private readonly HighSpeedZipArchiveService _zip = new();

    public ExtractionModesTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"CloudPacker_Extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testRoot, true); } catch { }
    }

    private async Task<string> CreateSampleZipAsync()
    {
        var src = Path.Combine(_testRoot, "Src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "file1.txt"), "Original v1");
        File.WriteAllText(Path.Combine(src, "file2.txt"), "Original v2");

        var zip = Path.Combine(_testRoot, "archive.zip");
        await _zip.CreateZipArchiveAsync(src, zip);
        return zip;
    }

    [Fact]
    public async Task Test_Safe_Mode_Creates_Timestamped_Folder()
    {
        var zip = await CreateSampleZipAsync();
        var target = Path.Combine(_testRoot, "SafeTarget");

        var res = await _zip.ExtractZipArchiveAsync(zip, target, writeMode: "safe");
        Assert.True(res.IsSuccess);

        // Find created Restore_* directory
        var restoreDirs = Directory.GetDirectories(target, "Restore_*");
        Assert.Single(restoreDirs);
        Assert.True(File.Exists(Path.Combine(restoreDirs[0], "file1.txt")));
    }

    [Fact]
    public async Task Test_Missing_Mode_Does_Not_Overwrite_Existing_Files()
    {
        var zip = await CreateSampleZipAsync();
        var target = Path.Combine(_testRoot, "MissingTarget");
        Directory.CreateDirectory(target);

        // Pre-create file1.txt with modified content
        File.WriteAllText(Path.Combine(target, "file1.txt"), "Locally Modified Content");

        var res = await _zip.ExtractZipArchiveAsync(zip, target, writeMode: "missing");
        Assert.True(res.IsSuccess);

        // file1.txt should NOT have been overwritten
        Assert.Equal("Locally Modified Content", File.ReadAllText(Path.Combine(target, "file1.txt")));
        // file2.txt should have been extracted
        Assert.True(File.Exists(Path.Combine(target, "file2.txt")));
    }

    [Fact]
    public async Task Test_Overwrite_Mode_Replaces_Existing_Files()
    {
        var zip = await CreateSampleZipAsync();
        var target = Path.Combine(_testRoot, "OverwriteTarget");
        Directory.CreateDirectory(target);

        // Pre-create file1.txt with outdated content
        File.WriteAllText(Path.Combine(target, "file1.txt"), "Old Content");

        var res = await _zip.ExtractZipArchiveAsync(zip, target, writeMode: "overwrite");
        Assert.True(res.IsSuccess);

        // file1.txt should have been overwritten with archive content
        Assert.Equal("Original v1", File.ReadAllText(Path.Combine(target, "file1.txt")));
    }
}
