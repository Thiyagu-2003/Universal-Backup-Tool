using UniBackup.Core.Models;
using UniBackup.Infrastructure.Sql;
using Xunit;

namespace UniBackup.Tests;

public class SqlBackupTextTests
{
    [Fact]
    public void FullBackup_DefaultOptions()
    {
        Assert.Equal(
            "BACKUP DATABASE @db TO DISK = @path WITH INIT, CHECKSUM, COMPRESSION, STATS = 5;",
            SqlBackupText.Backup(SqlBackupType.Full, new SqlServerSource(), compression: true));
    }

    [Fact]
    public void DiffBackup_NeverCopyOnly()
    {
        Assert.Equal(
            "BACKUP DATABASE @db TO DISK = @path WITH DIFFERENTIAL, INIT, CHECKSUM, STATS = 5;",
            SqlBackupText.Backup(SqlBackupType.Diff, new SqlServerSource { CopyOnly = true }, compression: false));
    }

    [Fact]
    public void LogBackup_CopyOnlyWithoutChecksum()
    {
        Assert.Equal(
            "BACKUP LOG @db TO DISK = @path WITH INIT, COPY_ONLY, COMPRESSION, STATS = 5;",
            SqlBackupText.Backup(SqlBackupType.Log, new SqlServerSource { CopyOnly = true, Checksum = false }, compression: true));
    }

    [Fact]
    public void VerifyAndCheckDb()
    {
        Assert.Equal("RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;", SqlBackupText.Verify(checksum: true));
        Assert.Equal("DBCC CHECKDB (@db) WITH NO_INFOMSGS, PHYSICAL_ONLY;", SqlBackupText.CheckDb(physicalOnly: true));
    }

    [Theory]
    [InlineData("10 percent processed.", true, 10)]
    [InlineData("100 percent processed.", true, 100)]
    [InlineData("Processed 344 pages for database 'Shop', file 'Shop' on file 1.", false, 0)]
    public void ParsesStatsMessages(string message, bool ok, int expected)
    {
        Assert.Equal(ok, SqlBackupText.TryParsePercent(message, out var pct));
        Assert.Equal(expected, pct);
    }
}
