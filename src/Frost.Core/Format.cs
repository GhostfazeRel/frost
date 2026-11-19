using System.Globalization;

namespace Frost.Core;

public static class Format
{
    private static readonly string[] Units = { "KB", "MB", "GB", "TB", "PB", "EB" };

    /// <summary>"0 bytes", "1 byte", "512 bytes", "1.5 KB", "12.3 MB", "1.02 GB" (1024-based, Explorer style).</summary>
    public static string Size(long bytes)
    {
        if (bytes == 1) return "1 byte";
        if (bytes < 1024) return $"{Math.Max(bytes, 0)} bytes";
        double v = bytes;
        int u = -1;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        // three significant digits, trailing zeros trimmed
        string fmt = v < 10 ? "0.##" : v < 100 ? "0.#" : "0";
        return v.ToString(fmt, CultureInfo.CurrentCulture) + " " + Units[u];
    }

    /// <summary>Relative for recent dates: "Just now", "5 min ago", "Today, 14:32", "Yesterday, 09:10",
    /// otherwise short date + time in current culture.</summary>
    public static string Date(DateTime local, DateTime now)
    {
        var diff = now - local;
        if (diff >= TimeSpan.Zero && diff < TimeSpan.FromMinutes(1)) return "Just now";
        if (diff >= TimeSpan.Zero && diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes} min ago";
        string time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == now.Date) return "Today, " + time;
        if (local.Date == now.Date.AddDays(-1)) return "Yesterday, " + time;
        return local.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>"Folder", or "PNG file" style fallback from extension, or "File".</summary>
    public static string TypeName(FileEntry entry)
    {
        if (entry.IsDirectory) return "Folder";
        var ext = entry.Extension;
        return ext.Length > 1 ? ext[1..].ToUpperInvariant() + " file" : "File";
    }
}
