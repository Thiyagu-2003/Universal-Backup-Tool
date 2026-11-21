using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlBackupFileNameTests
{
    [Fact]
    public void Format_ThenParse_RoundTrips()
    {
        var ts = new DateTime(2026, 11, 21, 14, 5, 9);
        var name = SqlBackupFileName.Format("Shop_DB", SqlBackupType.Log, ts);

        Assert.Equal("Shop_DB_LOG_20261121_140509.trn", name);
        Assert.True(SqlBackupFileName.TryParse(name, out var p));
        Assert.Equal("Shop_DB", p.Database);
        Assert.Equal(SqlBackupType.Log, p.Type);
        Assert.Equal(ts, p.Timestamp);
    }

    [Fact]
    public void Format_SanitizesInvalidFileNameCharacters()
    {
        Assert.Equal("a_b_FULL_20260101_000000.bak",
            SqlBackupFileName.Format("a:b", SqlBackupType.Full, new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void RemoteSubfolder_IsDatabaseThenType()
    {
        Assert.Equal("Shop/DIFF", SqlBackupFileName.RemoteSubfolder("Shop", SqlBackupType.Diff));
    }

    [Fact]
    public void TryParse_RejectsFolderArchives()
    {
        Assert.False(SqlBackupFileName.TryParse("Website_20261121_020000.zip", out _));
    }
}
