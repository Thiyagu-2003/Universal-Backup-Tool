using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlRestorePlannerTests
{
    [Fact]
    public void Plan_ReturnsLatestFull_WhenNoDiffOrLogExists()
    {
        var items = new List<CloudArchiveItem>
        {
            new() { DisplayName = "Shop_FULL_20261120_020000.bak", KeyOrId = "k_full1" },
            new() { DisplayName = "Shop_FULL_20261121_020000.bak", KeyOrId = "k_full2" }
        };

        var res = SqlRestorePlanner.Plan(items, "Shop", null);

        Assert.True(res.IsSuccess);
        var plan = res.Value!;
        Assert.Equal("k_full2", plan.FullBackup.KeyOrId);
        Assert.Null(plan.DiffBackup);
        Assert.Empty(plan.LogBackups);
        Assert.Single(plan.Sequence);
    }

    [Fact]
    public void Plan_IncludesDiffAndLogs_InChronologicalOrder()
    {
        var items = new List<CloudArchiveItem>
        {
            new() { DisplayName = "Shop_FULL_20261121_020000.bak", KeyOrId = "k_full" },
            new() { DisplayName = "Shop_LOG_20261121_030000.trn", KeyOrId = "k_log_old" }, // before diff, skipped
            new() { DisplayName = "Shop_DIFF_20261121_060000.bak", KeyOrId = "k_diff" },
            new() { DisplayName = "Shop_LOG_20261121_070000.trn", KeyOrId = "k_log1" },
            new() { DisplayName = "Shop_LOG_20261121_080000.trn", KeyOrId = "k_log2" }
        };

        var res = SqlRestorePlanner.Plan(items, "Shop", null);

        Assert.True(res.IsSuccess);
        var plan = res.Value!;
        Assert.Equal("k_full", plan.FullBackup.KeyOrId);
        Assert.Equal("k_diff", plan.DiffBackup?.KeyOrId);
        Assert.Equal(2, plan.LogBackups.Count);
        Assert.Equal("k_log1", plan.LogBackups[0].KeyOrId);
        Assert.Equal("k_log2", plan.LogBackups[1].KeyOrId);

        // Sequence order must be FULL -> DIFF -> LOG1 -> LOG2
        Assert.Equal(4, plan.Sequence.Count);
        Assert.Equal("k_full", plan.Sequence[0].KeyOrId);
        Assert.Equal("k_diff", plan.Sequence[1].KeyOrId);
        Assert.Equal("k_log1", plan.Sequence[2].KeyOrId);
        Assert.Equal("k_log2", plan.Sequence[3].KeyOrId);
    }

    [Fact]
    public void Plan_PointInTime_StopsAtCoveringLog()
    {
        var items = new List<CloudArchiveItem>
        {
            new() { DisplayName = "Shop_FULL_20261121_020000.bak", KeyOrId = "k_full" },
            new() { DisplayName = "Shop_DIFF_20261121_060000.bak", KeyOrId = "k_diff" },
            new() { DisplayName = "Shop_LOG_20261121_070000.trn", KeyOrId = "k_log1" },
            new() { DisplayName = "Shop_LOG_20261121_080000.trn", KeyOrId = "k_log2" },
            new() { DisplayName = "Shop_LOG_20261121_090000.trn", KeyOrId = "k_log3" },
            new() { DisplayName = "Shop_LOG_20261121_100000.trn", KeyOrId = "k_log4" }
        };

        // Target point in time: 08:30 (covered by the 09:00 log)
        var targetTime = new DateTime(2026, 11, 21, 8, 30, 0);
        var res = SqlRestorePlanner.Plan(items, "Shop", targetTime);

        Assert.True(res.IsSuccess);
        var plan = res.Value!;
        Assert.Equal("k_full", plan.FullBackup.KeyOrId);
        Assert.Equal("k_diff", plan.DiffBackup?.KeyOrId);

        // Should include logs 07:00, 08:00, 09:00, but NOT 10:00!
        Assert.Equal(3, plan.LogBackups.Count);
        Assert.Equal("k_log1", plan.LogBackups[0].KeyOrId);
        Assert.Equal("k_log2", plan.LogBackups[1].KeyOrId);
        Assert.Equal("k_log3", plan.LogBackups[2].KeyOrId);
    }

    [Fact]
    public void Plan_ReturnsFailure_WhenTargetBeforeAnyFull()
    {
        var items = new List<CloudArchiveItem>
        {
            new() { DisplayName = "Shop_FULL_20261121_020000.bak", KeyOrId = "k_full" }
        };

        var targetTime = new DateTime(2026, 11, 20, 12, 0, 0); // 1 day before FULL
        var res = SqlRestorePlanner.Plan(items, "Shop", targetTime);

        Assert.True(res.IsFailure);
        Assert.Contains("No FULL backup found", res.Error);
    }

    [Fact]
    public void Plan_IgnoresOtherDatabases()
    {
        var items = new List<CloudArchiveItem>
        {
            new() { DisplayName = "Orders_FULL_20261121_020000.bak", KeyOrId = "k_orders_full" },
            new() { DisplayName = "Shop_FULL_20261121_020000.bak", KeyOrId = "k_shop_full" }
        };

        var res = SqlRestorePlanner.Plan(items, "Shop", null);

        Assert.True(res.IsSuccess);
        Assert.Equal("k_shop_full", res.Value!.FullBackup.KeyOrId);
    }
}
