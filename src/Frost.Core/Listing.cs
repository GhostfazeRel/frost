using System.Buffers;

namespace Frost.Core;

public enum SortColumn { Name, Modified, Type, Size }

public static class DirectoryLister
{
    /// <summary>Lists a folder. Skips entries that throw (access denied, vanished). Throws
    /// DirectoryNotFoundException / UnauthorizedAccessException only for the folder itself.</summary>
    public static IReadOnlyList<FileEntry> List(string path, bool showHidden)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {path}");

        // IgnoreInaccessible also swallows an unreadable root, so probe it once with strict options.
        using (var probe = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).GetEnumerator())
            probe.MoveNext();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var result = new List<FileEntry>();
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            try { result.Add(FileEntry.FromInfo(info)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>Same as List, on a background thread.</summary>
    public static Task<IReadOnlyList<FileEntry>> ListAsync(string path, bool showHidden, CancellationToken ct = default)
        => Task.Run(() => List(path, showHidden), ct);
}

public static class Sorter
{
    /// <summary>Folders always first, then by column; ties broken by natural name order.</summary>
    public static IReadOnlyList<FileEntry> Sort(IEnumerable<FileEntry> items, SortColumn column, bool descending)
    {
        var arr = items.ToArray();
        string[]? types = null;
        if (column == SortColumn.Type)
        {
            types = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) types[i] = Format.TypeName(arr[i]);
        }
        var idx = new int[arr.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;

        Array.Sort(idx, (a, b) =>
        {
            var x = arr[a]; var y = arr[b];
            if (x.IsDirectory != y.IsDirectory) return x.IsDirectory ? -1 : 1;
            int c = column switch
            {
                SortColumn.Name => NaturalStringComparer.Instance.Compare(x.Name, y.Name),
                SortColumn.Modified => x.Modified.CompareTo(y.Modified),
                SortColumn.Type => string.Compare(types![a], types[b], StringComparison.OrdinalIgnoreCase),
                _ => x.Size.CompareTo(y.Size),
            };
            if (descending) c = -Math.Sign(c);
            if (c != 0) return c;
            c = NaturalStringComparer.Instance.Compare(x.Name, y.Name);
            return c != 0 ? c : a.CompareTo(b); // index tie-break makes the sort stable
        });

        var result = new FileEntry[arr.Length];
        for (int i = 0; i < idx.Length; i++) result[i] = arr[idx[i]];
        return result;
    }
}

/// <summary>Windows-Explorer-like natural order: "file2" before "file10", case-insensitive.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return CompareSpans(x, y);
    }

    private static int CompareSpans(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0, zeroTie = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var da = a[si..i]; var db = b[sj..j];
                var ta = da.TrimStart('0'); var tb = db.TrimStart('0');
                // Longer digit run (after zeros) is the bigger number; same length compares lexically. No parsing, no overflow.
                if (ta.Length != tb.Length) return ta.Length < tb.Length ? -1 : 1;
                int c = ta.SequenceCompareTo(tb);
                if (c != 0) return c < 0 ? -1 : 1;
                if (zeroTie == 0) zeroTie = (da.Length - ta.Length).CompareTo(db.Length - tb.Length); // fewer leading zeros first
            }
            else
            {
                if (ca != cb)
                {
                    char ua = char.ToUpperInvariant(ca), ub = char.ToUpperInvariant(cb);
                    if (ua != ub) return ua < ub ? -1 : 1;
                }
                i++; j++;
            }
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : zeroTie;
    }
}
