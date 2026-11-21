namespace UniBackup.Core.Common;

public static class AppPaths
{
    public const string AppDirName = "UniBackup";

    public static string AppBaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

    public static string ProgramDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppDirName);

    public static string SharedHomePointerFile =>
        Path.Combine(ProgramDataDirectory, "home.txt");

    public static string ResolveDataDirectory()
    {
        // 1. Check if ProgramData\UniBackup\home.txt specifies a custom location
        try
        {
            if (File.Exists(SharedHomePointerFile))
            {
                var custom = File.ReadAllText(SharedHomePointerFile).Trim();
                if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
                    return custom;
            }
        }
        catch
        {
            // Ignore error and fall back
        }

        // 2. Default data directory: C:\Users\UniBackup
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var defaultHome = Path.Combine(systemDrive, "Users", AppDirName);

        // 3. Fallback to LocalApplicationData if permissions prevent writing to C:\Users\UniBackup
        try
        {
            Directory.CreateDirectory(defaultHome);
            return defaultHome;
        }
        catch
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirName);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string DefaultConfigFile => Path.Combine(ResolveDataDirectory(), "config.json");
    public static string DefaultJobsFile => Path.Combine(ResolveDataDirectory(), "jobs.json");
    public static string DefaultHistoryFile => Path.Combine(ResolveDataDirectory(), "history.json");
    public static string DefaultLogsDirectory => Path.Combine(ResolveDataDirectory(), "logs");
    public static string DefaultTempDirectory => @"C:\UniBackup\temp";

    public static long GetFreeDiskSpaceBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    public static bool EnsureWritableDirectory(string path, out string finalPath)
    {
        finalPath = path;
        try
        {
            Directory.CreateDirectory(path);
            var testFile = Path.Combine(path, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            // Fall back to system temp folder
            var fallback = Path.Combine(Path.GetTempPath(), AppDirName);
            Directory.CreateDirectory(fallback);
            finalPath = fallback;
            return false;
        }
    }
}
