namespace Frost.Core;

/// <summary>Windows-style path parsing that behaves identically on every OS.</summary>
internal static class WinPath
{
    public static bool IsSep(char c) => c == '\\' || c == '/';

    /// <summary>Returns the length of the root prefix of <paramref name="p"/>, with the normalized root
    /// ("C:\", "\\server\share", "/", "") and the separator to use for children.</summary>
    public static int ParseRoot(string p, out string root, out char sep)
    {
        sep = '\\';
        if (p.Length >= 2 && IsSep(p[0]) && IsSep(p[1]))
        {
            int i = 2;
            while (i < p.Length && !IsSep(p[i])) i++;
            if (i < p.Length) { i++; while (i < p.Length && !IsSep(p[i])) i++; }
            root = "\\\\" + p[2..i].Replace('/', '\\');
            return i;
        }
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            root = p[..2] + "\\";
            return p.Length > 2 && IsSep(p[2]) ? 3 : 2;
        }
        if (IsSep(p[0]))
        {
            sep = p[0];
            root = p[..1];
            return 1;
        }
        sep = p.Contains('\\') ? '\\' : p.Contains('/') ? '/' : '\\';
        root = "";
        return 0;
    }

    public static List<(string Name, string Path)> Split(string path)
    {
        var res = new List<(string, string)>();
        if (string.IsNullOrEmpty(path)) return res;
        int rl = ParseRoot(path, out var root, out var sep);
        string cur = root;
        if (root.Length > 0) res.Add((root, root));
        var rest = path.AsSpan(rl);
        while (!rest.IsEmpty)
        {
            int k = rest.IndexOfAny('\\', '/');
            var part = (k < 0 ? rest : rest[..k]).ToString();
            rest = k < 0 ? default : rest[(k + 1)..];
            if (part.Length == 0) continue;
            cur = cur.Length == 0 ? part : cur[^1] == sep ? cur + part : cur + sep + part;
            res.Add((part, cur));
        }
        return res;
    }

    /// <summary>Canonical comparison key: normalized separators, no trailing separator (except roots).</summary>
    public static string Key(string path)
    {
        var s = Split(path);
        return s.Count == 0 ? "" : s[^1].Path;
    }
}

public static class PathUtil
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Breadcrumb segments, e.g. C:\Users\Bob -> [("C:\", "C:\"), ("Users","C:\Users"), ("Bob","C:\Users\Bob")].
    /// Must work with both '\' and '/' separators and UNC paths (\\server\share\dir).</summary>
    public static IReadOnlyList<(string Name, string Path)> Segments(string path) => WinPath.Split(path);

    /// <summary>"name.txt" -> "name (2).txt" -> "name (3).txt"... first name not existing in dir. Folders: "New folder (2)".</summary>
    public static string UniqueName(string directory, string desiredName)
    {
        static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
        string first = Path.Combine(directory, desiredName);
        if (!Exists(first)) return desiredName;

        string stem = desiredName, ext = "";
        if (!Directory.Exists(first))
        {
            ext = Path.GetExtension(desiredName);
            stem = desiredName[..^ext.Length];
            if (stem.Length == 0) { stem = desiredName; ext = ""; } // ".gitignore"
        }
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (!Exists(Path.Combine(directory, candidate))) return candidate;
        }
    }

    /// <summary>True if the name is valid for a Windows file/folder (no \/:*?"<>|, not reserved like CON/NUL, no trailing dot/space, not empty).</summary>
    public static bool IsValidFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (char c in name)
            if (c < 32 || "\\/:*?\"<>|".Contains(c)) return false;
        if (name[^1] == '.' || name[^1] == ' ') return false;
        int dot = name.IndexOf('.');
        return !Reserved.Contains(dot < 0 ? name : name[..dot].TrimEnd(' '));
    }
}

/// <summary>Back / forward / up history for one tab.</summary>
public sealed class NavigationHistory
{
    private readonly List<string> _items = new();
    private int _index;

    public NavigationHistory(string start) => _items.Add(start);

    public string Current => _items[_index];
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index < _items.Count - 1;

    /// <summary>Pushes a new location (no-op if equal to Current, ignoring case/trailing slash); clears forward stack.</summary>
    public void Navigate(string path)
    {
        if (string.Equals(WinPath.Key(path), WinPath.Key(Current), StringComparison.OrdinalIgnoreCase)) return;
        _items.RemoveRange(_index + 1, _items.Count - _index - 1);
        _items.Add(path);
        _index++;
    }

    /// <summary>Moves back and returns the new Current; returns Current unchanged if there is nothing to go back to.</summary>
    public string Back() { if (CanGoBack) _index--; return Current; }

    /// <summary>Moves forward and returns the new Current; returns Current unchanged if there is nothing ahead.</summary>
    public string Forward() { if (CanGoForward) _index++; return Current; }

    /// <summary>Parent of Current, or null at a drive root / special location.</summary>
    public string? Parent()
    {
        var segs = WinPath.Split(Current);
        return segs.Count <= 1 ? null : segs[^2].Path;
    }
}
