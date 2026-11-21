namespace UniBackup.Core.Common;

public static class FormattingUtils
{
    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        var mag = (int)Math.Max(0, Math.Min(Math.Floor(Math.Log(bytes, 1024)), SizeSuffixes.Length - 1));
        var adjusted = bytes / Math.Pow(1024, mag);
        return $"{adjusted:F2} {SizeSuffixes[mag]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
            return $"{Math.Max(1, (int)duration.TotalSeconds)}s";
        if (duration.TotalMinutes < 60)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{duration.Hours}h {duration.Minutes}m";
    }

    public static string FormatSpeed(long bytes, TimeSpan duration)
    {
        if (duration.TotalSeconds <= 0.1 || bytes <= 0) return "0 B/s";
        var bytesPerSec = bytes / duration.TotalSeconds;
        return $"{FormatBytes((long)bytesPerSec)}/s";
    }

    public static string SanitizePathSegment(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(input.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }
}
