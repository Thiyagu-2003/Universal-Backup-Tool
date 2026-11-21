namespace UniBackup.Core.Models;

public class IisSiteInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "Unknown"; // Started, Stopped
    public string PhysicalPath { get; set; } = "";
    public string AppPool { get; set; } = "";
    public List<string> Bindings { get; set; } = [];
    public bool PathExistsOnDisk { get; set; }
}
