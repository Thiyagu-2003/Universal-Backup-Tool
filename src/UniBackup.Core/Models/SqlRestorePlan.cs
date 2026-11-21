using UniBackup.Core.Models;

namespace UniBackup.Core.Models;

public class SqlRestorePlan
{
    public string Database { get; set; } = "";
    public DateTime? TargetTime { get; set; }
    public CloudArchiveItem FullBackup { get; set; } = null!;
    public CloudArchiveItem? DiffBackup { get; set; }
    public List<CloudArchiveItem> LogBackups { get; set; } = [];

    public List<CloudArchiveItem> Sequence
    {
        get
        {
            var list = new List<CloudArchiveItem> { FullBackup };
            if (DiffBackup != null) list.Add(DiffBackup);
            list.AddRange(LogBackups);
            return list;
        }
    }
}
