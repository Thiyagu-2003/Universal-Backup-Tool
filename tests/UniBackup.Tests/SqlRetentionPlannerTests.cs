using UniBackup.Core.Common;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlRetentionPlannerTests
{
    [Fact]
    public void Plan_NeverTouchesUnparsableNames()
    {
        var now = new DateTime(2026, 11, 21);
        var items = new List<CloudArchiveItem>
        {
            new() { DisplayName = "readme.txt", KeyOrId = "k1", SizeBytes = 100 },
            new() { DisplayName = "random_file.bak", KeyOrId = "k2", SizeBytes = 200 }
        };

        var plan = SqlRetentionPlanner.Plan(items, new RetentionPolicy { KeepDays = 7 }, now);

        Assert.Empty(plan.Deletions);
        Assert.All(plan.Retentions, r => Assert.Equal("UnparsableName", r.Reason));
    }

    [Fact]
    public void Plan_NeverDeletesTheOnlyFullOfDatabase()
    {
        var now = new DateTime(2026, 11, 21);
        var items = new List<CloudArchiveItem>
        {
            // Full is 100 days old, but it's the ONLY full for Shop
            new()
            {
                DisplayName = "Shop_FULL_20260801_020000.bak",
                KeyOrId = "s3://backups/Shop_FULL.bak",
                LastModified = now.AddDays(-100)
            }
        };

        var plan = SqlRetentionPlanner.Plan(items, new RetentionPolicy { KeepDays = 30 }, now);

        Assert.Empty(plan.Deletions);
        Assert.Single(plan.Retentions);
        Assert.Equal("KeepOnlyFull", plan.Retentions[0].Reason);
    }

    [Fact]
    public void Plan_KeepsOldFullAsAnchor_WhenYoungLogDependsOnIt()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0);
        var items = new List<CloudArchiveItem>
        {
            // FULL 1: 40 days old (older than 30-day retention cutoff)
            new()
            {
                DisplayName = "Shop_FULL_20261012_020000.bak",
                KeyOrId = "k_full1",
                LastModified = now.AddDays(-40)
            },
            // LOG 1: 1 day old (depends on FULL 1, within 30-day retention cutoff)
            new()
            {
                DisplayName = "Shop_LOG_20261120_120000.trn",
                KeyOrId = "k_log1",
                LastModified = now.AddDays(-1)
            }
        };

        var plan = SqlRetentionPlanner.Plan(items, new RetentionPolicy { KeepDays = 30 }, now);

        // Neither should be deleted: Log is within keep_days, Full is kept as an anchor!
        Assert.Empty(plan.Deletions);
        var fullAction = plan.Retentions.First(r => r.Item.KeyOrId == "k_full1");
        Assert.Equal("KeepAnchor", fullAction.Reason);
    }

    [Fact]
    public void Plan_DeletesOldChain_WhenNewerFullExistsAndOldChainExpired()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0);
        var items = new List<CloudArchiveItem>
        {
            // Chain 1: 50 days old Full + 45 days old Diff
            new() { DisplayName = "Shop_FULL_20261001_020000.bak", KeyOrId = "k_old_full", LastModified = now.AddDays(-50) },
            new() { DisplayName = "Shop_DIFF_20261006_020000.bak", KeyOrId = "k_old_diff", LastModified = now.AddDays(-45) },

            // Chain 2: 5 days old Full + 2 days old Diff
            new() { DisplayName = "Shop_FULL_20261116_020000.bak", KeyOrId = "k_new_full", LastModified = now.AddDays(-5) },
            new() { DisplayName = "Shop_DIFF_20261119_020000.bak", KeyOrId = "k_new_diff", LastModified = now.AddDays(-2) }
        };

        var plan = SqlRetentionPlanner.Plan(items, new RetentionPolicy { KeepDays = 30 }, now);

        // Old Full and Old Diff should be deleted
        Assert.Equal(2, plan.Deletions.Count);
        Assert.Contains(plan.Deletions, d => d.Item.KeyOrId == "k_old_full" && d.Reason == "AgedOut");
        Assert.Contains(plan.Deletions, d => d.Item.KeyOrId == "k_old_diff" && d.Reason == "Superseded");

        // New Full and New Diff must be retained
        Assert.Equal(2, plan.Retentions.Count);
        Assert.All(plan.Retentions, r => Assert.Equal("KeepWithinWindow", r.Reason));
    }

    [Fact]
    public void Plan_GFS_KeepsWeeklyAndMonthlyFulls()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0); // Nov 21
        var items = new List<CloudArchiveItem>
        {
            // Full 1: 60 days old (from Sept, normally aged out under 30d retention, but 1st monthly full)
            new() { DisplayName = "Shop_FULL_20260920_020000.bak", KeyOrId = "k_monthly_full", LastModified = now.AddDays(-60) },
            // Full 2: 5 days old
            new() { DisplayName = "Shop_FULL_20261116_020000.bak", KeyOrId = "k_current_full", LastModified = now.AddDays(-5) }
        };

        var policy = new RetentionPolicy
        {
            KeepDays = 30,
            GfsEnabled = true,
            GfsKeepMonthlies = 3
        };

        var plan = SqlRetentionPlanner.Plan(items, policy, now);

        Assert.Empty(plan.Deletions);
        Assert.Contains(plan.Retentions, r => r.Item.KeyOrId == "k_monthly_full" && r.Reason == "KeepGfsMonthly");
    }
}
