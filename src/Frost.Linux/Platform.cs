using System.Diagnostics;
using System.Text.Json;
using Frost.Core;
namespace Frost.Linux;

public enum ViewMode { Details, Tiles, Large, ExtraLarge }

public sealed class AppSettings
{
    public List<string> Pinned { get; set; } = new();
    public ViewMode View { get; set; } = ViewMode.Details;
    public double IconSize { get; set; } = 64;
    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public bool ShowHidden { get; set; }
    public bool PreviewPane { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public List<string> OpenTabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public List<string> RecentFolders { get; set; } = new();
}

/// <summary>Everything that differs from Windows: settings location, trash, xdg-open, terminal, places and drives.</summary>
public static class Platform
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Linux paths are case-sensitive.
    public static bool Same(string? a, string? b) => a?.TrimEnd('/') == b?.TrimEnd('/');

    // ---- settings: $XDG_CONFIG_HOME/frost/settings.json ----
    static string ConfigDir => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "frost");
    static string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    public static AppSettings LoadSettings()
    {
        try { if (File.Exists(SettingsFile)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { try { File.Copy(SettingsFile, SettingsFile + ".bak", true); } catch { } } // keep the unreadable file: the next save overwrites it
        return new();
    }

    public static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsFile, true);
        }
        catch { }
    }

    /// <summary>A folder path or file:// URI from the command line; a file opens its folder.</summary>
    public static string? StartPath(string arg)
    {
        try
        {
            var p = arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(arg).LocalPath : Path.GetFullPath(arg);
            if (File.Exists(p)) p = Path.GetDirectoryName(p)!;
            return Directory.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    // ---- processes (always ArgumentList, never a shell string) ----
    // Only redirect when we read the output: an unread pipe blocks a chatty child (xdg-open passes it on to the app).
    static Process? Start(string file, IEnumerable<string> args, string? cwd = null, bool capture = false)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = capture, RedirectStandardOutput = capture };
        if (cwd != null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }

    /// <summary>Moves to the trash with gio; never deletes permanently. False when gio failed.</summary>
    public static bool Trash(IReadOnlyList<string> paths)
    {
        try
        {
            using var p = Start("gio", new[] { "trash", "--" }.Concat(paths), capture: true);
            p!.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static void Open(string path) => Start("xdg-open", new[] { path })?.Dispose();
    public static void OpenTrash() { try { Start("gio", new[] { "open", "trash:///" })?.Dispose(); } catch { } }

    public static void OpenTerminal(string folder)
    {
        var t = (Environment.GetEnvironmentVariable("TERMINAL") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0)
        {
            var found = new[] { "kitty", "alacritty", "foot", "wezterm", "ghostty", "gnome-terminal", "kgx", "konsole", "xfce4-terminal", "xterm" }.FirstOrDefault(OnPath);
            if (found == null) throw new InvalidOperationException("no terminal found; set $TERMINAL");
            t = new[] { found };
        }
        Start(t[0], t.Skip(1), folder)?.Dispose();
    }

    // ---- default file manager (same files as packaging/linux/install.sh, so either can undo the other) ----
    const string DesktopId = "frost.desktop";
    static string DataDir => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share");
    static string PreviousDefaultFile => Path.Combine(DataDir, "frost", "previous-default");
    /// <summary>Only an installed Frost has a .desktop file that xdg-mime can point folders at.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(DataDir, "applications", DesktopId));

    static string? XdgMime(params string[] args)
    {
        try
        {
            using var p = Start("xdg-mime", args, capture: true)!;
            var output = p.StandardOutput.ReadToEnd().Trim(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    public static bool IsDefaultFileManager() => XdgMime("query", "default", "inode/directory") == DesktopId;

    /// <summary>Makes Frost the folder handler, remembering the old one once. Returns an error or null.</summary>
    public static string? MakeDefaultFileManager()
    {
        var prev = XdgMime("query", "default", "inode/directory");
        if (!File.Exists(PreviousDefaultFile) && prev != DesktopId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreviousDefaultFile)!);
            File.WriteAllText(PreviousDefaultFile, (prev ?? "") + "\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigDir)!); // xdg-mime silently writes nothing without it
        return XdgMime("default", DesktopId, "inode/directory") != null && IsDefaultFileManager() ? null : "xdg-mime failed (is xdg-utils installed?)";
    }

    /// <summary>Gives folders back to the file manager that had them before. Returns an error or null.</summary>
    public static string? RestoreDefaultFileManager()
    {
        var prev = File.Exists(PreviousDefaultFile) ? File.ReadAllText(PreviousDefaultFile).Trim() : "";
        if (prev.Length == 0) return "No previous file manager is known; pick one in your system settings.";
        if (XdgMime("default", prev, "inode/directory") == null) return "xdg-mime failed (is xdg-utils installed?)";
        File.Delete(PreviousDefaultFile);
        return null;
    }

    static bool OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));

    // ---- places ----
    public static List<(string Name, string Path)> QuickAccess()
    {
        var list = new List<(string, string)> { ("Home", Home) };
        var dirs = new Dictionary<string, string>();
        try
        {
            foreach (var l in File.ReadLines(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "user-dirs.dirs")))
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, "^XDG_(\\w+)_DIR=\"(.*)\"");
                if (m.Success) dirs[m.Groups[1].Value] = m.Groups[2].Value.Replace("$HOME", Home);
            }
        }
        catch { }
        foreach (var (key, name) in new[] { ("DESKTOP", "Desktop"), ("DOWNLOAD", "Downloads"), ("DOCUMENTS", "Documents"), ("PICTURES", "Pictures"), ("MUSIC", "Music"), ("VIDEOS", "Videos") })
        {
            var p = dirs.GetValueOrDefault(key) ?? Path.Combine(Home, name == "Videos" ? "Videos" : name);
            if (Directory.Exists(p) && !Same(p, Home)) list.Add((name, p));
        }
        return list;
    }

    public static List<(string Name, string Path, string Free)> Drives()
    {
        var list = new List<(string, string, string)> { ("Root", "/", Free("/")) };
        try
        {
            foreach (var l in File.ReadLines("/proc/mounts"))
            {
                var f = l.Split(' ');
                if (f.Length < 2 || !f[0].StartsWith("/dev/") || f[0].StartsWith("/dev/loop") || f[0].StartsWith("/dev/zram")) continue;
                var mp = f[1].Replace("\\040", " ").Replace("\\011", "\t"); // /proc/mounts escapes spaces
                if (!new[] { "/mnt/", "/media/", "/run/media/" }.Any(x => mp.StartsWith(x)) || list.Any(d => d.Item2 == mp)) continue;
                list.Add((Path.GetFileName(mp) is { Length: > 0 } n ? n : mp, mp, Free(mp)));
            }
        }
        catch { }
        return list;
    }

    static string Free(string p)
    {
        try { var d = new DriveInfo(p); return $"{Format.Size(d.AvailableFreeSpace)} free of {Format.Size(d.TotalSize)}"; }
        catch { return ""; }
    }
}
