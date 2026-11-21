using UniBackup.Core.Common;
using Xunit;

namespace UniBackup.Tests;

public class AppPathsAndDiskSafetyTests
{
    [Fact]
    public void Test_FormatBytes()
    {
        Assert.Equal("0 B", FormattingUtils.FormatBytes(0));
        Assert.Equal("512.00 B", FormattingUtils.FormatBytes(512));
        Assert.Equal("1.00 KB", FormattingUtils.FormatBytes(1024));
        Assert.Equal("1.50 MB", FormattingUtils.FormatBytes((long)(1.5 * 1024 * 1024)));
        Assert.Equal("2.25 GB", FormattingUtils.FormatBytes((long)(2.25 * 1024 * 1024 * 1024)));
        Assert.Equal("1.00 TB", FormattingUtils.FormatBytes((long)(1024L * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void Test_FormatDuration()
    {
        Assert.Equal("45s", FormattingUtils.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("3m 12s", FormattingUtils.FormatDuration(TimeSpan.FromSeconds(192)));
        Assert.Equal("1h 15m", FormattingUtils.FormatDuration(TimeSpan.FromMinutes(75)));
    }

    [Fact]
    public void Test_FormatSpeed()
    {
        var bytes = 10 * 1024 * 1024; // 10 MB
        var duration = TimeSpan.FromSeconds(2); // 2s -> 5 MB/s
        var speedStr = FormattingUtils.FormatSpeed(bytes, duration);

        Assert.Equal("5.00 MB/s", speedStr);
    }

    [Fact]
    public void Test_SanitizePathSegment()
    {
        var dirty = "My Project : In-Progress? *2026* <test> | \\ / \"";
        var clean = FormattingUtils.SanitizePathSegment(dirty);

        Assert.DoesNotContain(":", clean);
        Assert.DoesNotContain("?", clean);
        Assert.DoesNotContain("*", clean);
        Assert.DoesNotContain("<", clean);
        Assert.DoesNotContain(">", clean);
        Assert.DoesNotContain("|", clean);
        Assert.DoesNotContain("\"", clean);
    }

    [Fact]
    public void Test_EnsureWritableDirectory_Valid()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"CloudPacker_Writable_{Guid.NewGuid():N}");
        try
        {
            var success = AppPaths.EnsureWritableDirectory(temp, out var finalPath);
            Assert.True(success);
            Assert.Equal(temp, finalPath);
            Assert.True(Directory.Exists(finalPath));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }
}
