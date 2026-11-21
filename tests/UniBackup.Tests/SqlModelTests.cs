using System.Text.Json;
using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class SqlModelTests
{
    [Fact]
    public void LegacyJobJson_LoadsAsFolderJob()
    {
        var job = JsonSerializer.Deserialize<BackupJob>("{\"id\":\"a1\",\"name\":\"Web\",\"source_folder\":\"C:\\\\inetpub\"}")!;

        Assert.False(job.IsSqlServer);
        Assert.Equal("folder", job.Kind);
        Assert.Null(job.Sql);
    }

    [Fact]
    public void SqlJob_RoundTrips()
    {
        var job = new BackupJob
        {
            Kind = "sqlserver",
            Sql = new SqlServerSource { AllUserDatabases = false, Databases = ["Shop"] }
        };

        var back = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(job))!;

        Assert.True(back.IsSqlServer);
        Assert.Equal(new[] { "Shop" }, back.Sql!.Databases);
        Assert.True(back.Sql.Checksum);
    }

    [Fact]
    public void BackupType_SerializesAsString()
    {
        var history = new RunHistoryRecord
        {
            SqlDatabases = [new SqlDatabaseRunResult { Database = "Shop", BackupType = SqlBackupType.Diff }]
        };

        Assert.Contains("\"backup_type\":\"Diff\"", JsonSerializer.Serialize(history));
    }
}
