namespace UniBackup.Core.Models;

public record ServiceHealth(string Status, string Service, string Version, DateTime Timestamp);
