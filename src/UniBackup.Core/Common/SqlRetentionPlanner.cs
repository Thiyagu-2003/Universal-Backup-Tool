using System.Globalization;
using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class SqlRetentionPlanner
{
    private record ParsedSqlArchive(CloudArchiveItem Item, string Database, SqlBackupType Type, DateTime Timestamp);
    private record SqlBackupChain(ParsedSqlArchive Full, List<ParsedSqlArchive> Dependents);

    public static SqlRetentionPlan Plan(IEnumerable<CloudArchiveItem> items, RetentionPolicy policy, DateTime now)
    {
        var plan = new SqlRetentionPlan();
        var itemList = items.ToList();

        var parsedItems = new List<ParsedSqlArchive>();
        foreach (var item in itemList)
        {
            if (SqlBackupFileName.TryParse(item.DisplayName, out var parsed))
            {
                parsedItems.Add(new ParsedSqlArchive(item, parsed.Database, parsed.Type, parsed.Timestamp));
            }
            else
            {
                plan.Actions.Add(new RetentionAction
                {
                    Item = item,
                    Delete = false,
                    Reason = "UnparsableName"
                });
            }
        }

        var dbGroups = parsedItems.GroupBy(p => p.Database, StringComparer.OrdinalIgnoreCase);

        foreach (var dbGroup in dbGroups)
        {
            var sorted = dbGroup.OrderBy(p => p.Timestamp).ToList();
            var fulls = sorted.Where(p => p.Type == SqlBackupType.Full).ToList();

            if (fulls.Count == 0)
            {
                var orphanCutoff = now.AddDays(-policy.KeepDays);
                foreach (var p in sorted)
                {
                    var isOld = p.Timestamp < orphanCutoff;
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = p.Item,
                        Delete = isOld,
                        Reason = isOld ? "OrphanAgedOut" : "KeepWithinWindow"
                    });
                }
                continue;
            }

            bool onlyOneFull = fulls.Count == 1;

            var chains = new List<SqlBackupChain>();
            for (var i = 0; i < fulls.Count; i++)
            {
                var curFull = fulls[i];
                var nextFullTimestamp = (i + 1 < fulls.Count) ? fulls[i + 1].Timestamp : DateTime.MaxValue;
                var dependents = sorted
                    .Where(p => p.Type != SqlBackupType.Full && p.Timestamp >= curFull.Timestamp && p.Timestamp < nextFullTimestamp)
                    .ToList();
                chains.Add(new SqlBackupChain(curFull, dependents));
            }

            var gfsMonthlyFulls = new HashSet<CloudArchiveItem>();
            var gfsWeeklyFulls = new HashSet<CloudArchiveItem>();

            if (policy.GfsEnabled)
            {
                if (policy.GfsKeepMonthlies > 0)
                {
                    var byMonth = fulls
                        .GroupBy(f => new DateTime(f.Timestamp.Year, f.Timestamp.Month, 1))
                        .OrderByDescending(g => g.Key)
                        .Take(policy.GfsKeepMonthlies);
                    foreach (var monthGroup in byMonth)
                    {
                        var candidate = monthGroup.OrderBy(f => f.Timestamp).First();
                        gfsMonthlyFulls.Add(candidate.Item);
                    }
                }

                if (policy.GfsKeepWeeklies > 0)
                {
                    var byWeek = fulls
                        .GroupBy(f => CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(f.Timestamp, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday) + "_" + f.Timestamp.Year)
                        .OrderByDescending(g => g.Key)
                        .Take(policy.GfsKeepWeeklies);
                    foreach (var weekGroup in byWeek)
                    {
                        var candidate = weekGroup.OrderBy(f => f.Timestamp).First();
                        if (!gfsMonthlyFulls.Contains(candidate.Item))
                        {
                            gfsWeeklyFulls.Add(candidate.Item);
                        }
                    }
                }
            }

            var cutoffDate = now.AddDays(-policy.KeepDays);

            for (var cIndex = 0; cIndex < chains.Count; cIndex++)
            {
                var chain = chains[cIndex];
                bool isLatestChain = (cIndex == chains.Count - 1);
                var fullItem = chain.Full;

                var retainedDependents = new List<(CloudArchiveItem Item, string Reason)>();
                var deletedDependents = new List<(CloudArchiveItem Item, string Reason)>();

                foreach (var dep in chain.Dependents)
                {
                    bool depWithinWindow = dep.Timestamp >= cutoffDate || (dep.Item.LastModified.HasValue && dep.Item.LastModified.Value >= cutoffDate);
                    if (depWithinWindow)
                    {
                        retainedDependents.Add((dep.Item, "KeepWithinWindow"));
                    }
                    else
                    {
                        string reason = dep.Type == SqlBackupType.Diff ? "Superseded" : "AgedOut";
                        deletedDependents.Add((dep.Item, reason));
                    }
                }

                bool fullWithinWindow = fullItem.Timestamp >= cutoffDate || (fullItem.Item.LastModified.HasValue && fullItem.Item.LastModified.Value >= cutoffDate);

                if (fullWithinWindow)
                {
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = fullItem.Item,
                        Delete = false,
                        Reason = "KeepWithinWindow"
                    });
                }
                else if (gfsMonthlyFulls.Contains(fullItem.Item))
                {
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = fullItem.Item,
                        Delete = false,
                        Reason = "KeepGfsMonthly"
                    });
                }
                else if (gfsWeeklyFulls.Contains(fullItem.Item))
                {
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = fullItem.Item,
                        Delete = false,
                        Reason = "KeepGfsWeekly"
                    });
                }
                else if (retainedDependents.Count > 0)
                {
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = fullItem.Item,
                        Delete = false,
                        Reason = "KeepAnchor"
                    });
                }
                else if (onlyOneFull || isLatestChain)
                {
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = fullItem.Item,
                        Delete = false,
                        Reason = "KeepOnlyFull"
                    });
                }
                else
                {
                    plan.Actions.Add(new RetentionAction
                    {
                        Item = fullItem.Item,
                        Delete = true,
                        Reason = "AgedOut"
                    });
                }

                bool fullDeleted = plan.Actions.First(a => a.Item.KeyOrId == fullItem.Item.KeyOrId).Delete;
                if (fullDeleted)
                {
                    foreach (var dep in chain.Dependents)
                    {
                        string reason = dep.Type == SqlBackupType.Diff ? "Superseded" : "AgedOut";
                        plan.Actions.Add(new RetentionAction
                        {
                            Item = dep.Item,
                            Delete = true,
                            Reason = reason
                        });
                    }
                }
                else
                {
                    foreach (var (item, reason) in retainedDependents)
                    {
                        plan.Actions.Add(new RetentionAction { Item = item, Delete = false, Reason = reason });
                    }
                    foreach (var (item, reason) in deletedDependents)
                    {
                        plan.Actions.Add(new RetentionAction { Item = item, Delete = true, Reason = reason });
                    }
                }
            }
        }

        return plan;
    }
}
