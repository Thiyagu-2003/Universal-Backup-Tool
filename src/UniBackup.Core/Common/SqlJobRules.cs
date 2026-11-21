using UniBackup.Core.Models;

namespace UniBackup.Core.Common;

public static class SqlJobRules
{
    /// A DIFF needs a FULL base and a LOG needs an unbroken log chain, and that base must be a file this job uploaded to
    /// every destination (uploadedFiles = file names). A base taken by another tool, or one whose upload failed, would leave
    /// the cloud copy unrestorable, so take a FULL instead.
    public static SqlBackupType EffectiveType(SqlBackupType requested, SqlChainState chain, IReadOnlySet<string> uploadedFiles)
    {
        bool InCloud(string? device) => device != null && uploadedFiles.Contains(Path.GetFileName(device));

        return requested switch
        {
            SqlBackupType.Diff when !chain.HasDifferentialBase || !InCloud(chain.LastFullFile) => SqlBackupType.Full,
            SqlBackupType.Log when !chain.HasLogChain || !InCloud(chain.LastFullOrLogFile) => SqlBackupType.Full,
            _ => requested
        };
    }

    public static string OverallStatus(IReadOnlyCollection<SqlDatabaseRunResult> databases, IReadOnlyCollection<DestinationRunResult> uploads)
    {
        var succeeded = databases.Count(d => d.Status == "SUCCESS");
        var failed = databases.Count(d => d.Status == "FAILED") + uploads.Count(u => u.Status == "FAILED");

        if (succeeded == 0 || uploads.All(u => u.Status != "SUCCESS")) return "FAILED";
        return failed > 0 ? "PARTIAL" : "SUCCESS";
    }
}
