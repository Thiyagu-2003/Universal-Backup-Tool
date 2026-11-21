using UniBackup.Core.Models;
using Xunit;

namespace UniBackup.Tests;

public class ConfigAndJobTests
{
    [Fact]
    public void Test_Job_Archive_Name_Generation()
    {
        var job = new BackupJob
        {
            Name = "MyWebsite",
            SourceFolder = @"C:\inetpub\wwwroot\site1"
        };

        var time = new DateTime(2026, 11, 17, 14, 30, 0);
        var archiveName = job.GenerateArchiveName(time);

        Assert.Equal("MyWebsite_20261117_143000.zip", archiveName);
    }

    [Fact]
    public void Test_Schedule_Display_String()
    {
        var daily = new JobSchedule { Freq = "daily", Time = "03:15", Interval = 1 };
        Assert.Equal("Daily at 03:15", daily.ToDisplayString());

        var weekly = new JobSchedule { Freq = "weekly", Time = "02:00", Days = ["MON", "WED", "FRI"] };
        Assert.Equal("Weekly on MON, WED, FRI at 02:00", weekly.ToDisplayString());

        var manual = new JobSchedule { Freq = "manual" };
        Assert.Equal("Manual only", manual.ToDisplayString());
    }

    [Fact]
    public void Test_ServerIdentity_S3_Prefix()
    {
        var server = ServerIdentity.Current;
        Assert.NotNull(server.Hostname);
        Assert.NotNull(server.IpAddress);
        Assert.StartsWith("backups/", server.DefaultS3Prefix);
        Assert.EndsWith("/", server.DefaultS3Prefix);
    }
}
