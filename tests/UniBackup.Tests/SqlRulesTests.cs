using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlRulesTests
{
    private static readonly SqlDatabaseInfo[] Server =
    [
        new("master", "ONLINE", "SIMPLE", false, true),
        new("msdb", "ONLINE", "SIMPLE", false, true),
        new("Shop", "ONLINE", "FULL", false, false),
        new("Shop_Archive", "ONLINE", "FULL", true, false),
        new("Reports", "ONLINE", "SIMPLE", false, false),
        new("OldCrm", "OFFLINE", "FULL", false, false),
    ];

    [Fact]
    public void AllUserDatabases_ExcludesSystemAndWildcards_SkipsOffline()
    {
        var (included, skipped) = SqlDatabaseSelector.Select(Server, new SqlServerSource { ExcludePatterns = ["*_archive"] }, SqlBackupType.Full);

        Assert.Equal(new[] { "Shop", "Reports" }, included);
        var s = Assert.Single(skipped);
        Assert.Equal("OldCrm", s.Database);
        Assert.Equal("SKIPPED", s.Status);
    }

    [Fact]
    public void LogBackup_SkipsSimpleRecovery()
    {
        var (included, skipped) = SqlDatabaseSelector.Select(Server, new SqlServerSource(), SqlBackupType.Log);

        Assert.Equal(new[] { "Shop", "Shop_Archive" }, included);
        Assert.Contains(skipped, x => x.Database == "Reports" && x.Message!.Contains("SIMPLE"));
    }

    [Fact]
    public void ExplicitList_AllowsSystemDb_AndFailsMissingOnes()
    {
        var src = new SqlServerSource { AllUserDatabases = false, Databases = ["master", "Ghost"] };

        var (included, skipped) = SqlDatabaseSelector.Select(Server, src, SqlBackupType.Diff);

        Assert.Empty(included); // master supports full backups only
        Assert.Contains(skipped, x => x.Database == "master" && x.Status == "SKIPPED");
        Assert.Contains(skipped, x => x.Database == "Ghost" && x.Status == "FAILED");
    }

    [Theory]
    [InlineData(SqlBackupType.Full, false, false, SqlBackupType.Full)]
    [InlineData(SqlBackupType.Diff, false, true, SqlBackupType.Full)]
    [InlineData(SqlBackupType.Diff, true, true, SqlBackupType.Diff)]
    [InlineData(SqlBackupType.Log, true, false, SqlBackupType.Full)]
    [InlineData(SqlBackupType.Log, true, true, SqlBackupType.Log)]
    public void EffectiveType_PromotesToFullWhenChainMissing(SqlBackupType requested, bool diffBase, bool logChain, SqlBackupType expected)
    {
        var chain = new SqlChainState(diffBase, logChain, @"D:\Backup\UniBackup\j1\Shop_FULL_20261121_010000.bak", @"D:\Backup\UniBackup\j1\Shop_LOG_20261121_011500.trn");
        Assert.Equal(expected, SqlJobRules.EffectiveType(requested, chain, Uploaded));
    }

    private static readonly HashSet<string> Uploaded = ["Shop_FULL_20261121_010000.bak", "Shop_LOG_20261121_011500.trn"];

    [Theory]
    [InlineData(SqlBackupType.Diff, @"E:\Maint\Shop_maint.bak", "Shop_LOG_20261121_011500.trn")] // another tool took the newest FULL
    [InlineData(SqlBackupType.Diff, @"D:\Backup\UniBackup\j1\Shop_FULL_20261122_010000.bak", "x.trn")] // our FULL, but its upload failed
    [InlineData(SqlBackupType.Log, "Shop_FULL_20261121_010000.bak", @"E:\Maint\Shop_maint.trn")] // another tool took a LOG: gap in our chain
    [InlineData(SqlBackupType.Diff, null, null)] // msdb history was purged
    public void EffectiveType_PromotesToFull_WhenBaseIsNotInTheCloud(SqlBackupType requested, string? lastFull, string? lastFullOrLog)
    {
        Assert.Equal(SqlBackupType.Full, SqlJobRules.EffectiveType(requested, new SqlChainState(true, true, lastFull, lastFullOrLog), Uploaded));
    }

    private static SqlDatabaseRunResult Db(string status) => new() { Status = status };
    private static DestinationRunResult Up(string status) => new() { Status = status };

    [Fact]
    public void OverallStatus()
    {
        Assert.Equal("SUCCESS", SqlJobRules.OverallStatus([Db("SUCCESS"), Db("SKIPPED")], [Up("SUCCESS")]));
        Assert.Equal("PARTIAL", SqlJobRules.OverallStatus([Db("SUCCESS"), Db("FAILED")], [Up("SUCCESS")]));
        Assert.Equal("PARTIAL", SqlJobRules.OverallStatus([Db("SUCCESS")], [Up("SUCCESS"), Up("FAILED")]));
        Assert.Equal("FAILED", SqlJobRules.OverallStatus([Db("FAILED")], []));
        Assert.Equal("FAILED", SqlJobRules.OverallStatus([Db("SUCCESS")], [Up("FAILED")]));
    }
}
