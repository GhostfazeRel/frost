using System.Globalization;
using System.Text.RegularExpressions;

namespace Frost.Core;

public enum BatchCase { Unchanged, Lower, Upper, Title }

/// <summary>Order of work: find/replace, prefix + suffix, {n} numbering, case. "{n}" works in Replace, Prefix and Suffix.</summary>
public sealed record BatchRenameOptions
{
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Width { get; init; } = 1;
    public BatchCase Case { get; init; }
    /// <summary>False: only the name before the last dot changes (folders always change whole).</summary>
    public bool IncludeExtension { get; init; }
}

public sealed record BatchRenameItem(string OldPath, string NewName, string? Error)
{
    public string OldName => Path.GetFileName(OldPath);
}

public static class BatchRename
{
    /// <summary>Computes new names without touching the disk except for one directory listing per folder involved.
    /// A regex that times out (100 ms per match) fails the whole batch with "pattern too slow" instead of retrying per item.
    /// Honors cancellation (OperationCanceledException).</summary>
    public static IReadOnlyList<BatchRenameItem> Plan(IReadOnlyList<string> paths, BatchRenameOptions o, CancellationToken ct = default)
    {
        var src = paths.Select(Path.TrimEndingDirectorySeparator).ToArray();
        Regex? rx = null;
        string? optionError = null;
        if (o.Find.Length > 0)
        {
            try
            {
                rx = new Regex(o.UseRegex ? o.Find : Regex.Escape(o.Find),
                    o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException) { optionError = "Invalid regular expression."; }
        }

        // One listing per directory: name -> isDirectory.
        var listings = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> List(string dir)
        {
            if (listings.TryGetValue(dir, out var l)) return l;
            l = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos()) l[e.Name] = e is DirectoryInfo; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return listings[dir] = l;
        }

        var names = new string[src.Length];
        var errors = new string?[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(src[i]);
            errors[i] = optionError;
            bool isDir = List(Path.GetDirectoryName(src[i]) ?? "").TryGetValue(name, out var d) && d;
            try { names[i] = Transform(name, isDir, i, o, rx); }
            catch (RegexMatchTimeoutException)
            {
                return src.Select(p => new BatchRenameItem(p, Path.GetFileName(p), "Pattern too slow; simplify the regular expression.")).ToList();
            }
            catch (RegexParseException) { names[i] = name; errors[i] ??= "Invalid replacement."; }
        }

        var sources = new HashSet<string>(src, StringComparer.OrdinalIgnoreCase);
        var targets = src.Select((p, i) => Path.Combine(Path.GetDirectoryName(p) ?? "", names[i])).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets) counts[t] = counts.GetValueOrDefault(t) + 1;

        var result = new List<BatchRenameItem>(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? err = errors[i];
            if (err == null)
            {
                if (names[i].Length == 0) err = "The name would be empty.";
                else if (!PathUtil.IsValidFileName(names[i]))
                    err = names[i].Any(c => c < 32 || "\\/:*?\"<>|".Contains(c))
                        ? "The name contains characters that aren't allowed: \\ / : * ? \" < > |"
                        : "The name is reserved or ends with a dot or space.";
                else if (counts[targets[i]] > 1) err = "Another item in this batch gets the same name.";
                // exists on disk and isn't one of the items being renamed (those move away first; unchanged ones are caught above)
                else if (!sources.Contains(targets[i]) && List(Path.GetDirectoryName(targets[i]) ?? "").ContainsKey(names[i]))
                    err = "An item with this name already exists.";
            }
            result.Add(new BatchRenameItem(src[i], names[i], err));
        }
        return result;
    }

    private static string Transform(string name, bool isDir, int index, BatchRenameOptions o, Regex? rx)
    {
        string ext = "";
        string text = name;
        if (!o.IncludeExtension && !isDir)
        {
            ext = Path.GetExtension(name);
            text = name[..^ext.Length];
        }
        // {n} is substituted only in the user's own strings, never in the original file name.
        string n = (o.Start + (long)index * o.Step).ToString("D" + Math.Clamp(o.Width, 1, 18), CultureInfo.InvariantCulture);
        string Num(string s) => s.Replace("{n}", n);
        if (rx != null) text = rx.Replace(text, o.UseRegex ? Num(o.Replace) : Num(o.Replace).Replace("$", "$$"));
        text = Num(o.Prefix) + text + Num(o.Suffix);
        text = o.Case switch
        {
            BatchCase.Lower => text.ToLowerInvariant(),
            BatchCase.Upper => text.ToUpperInvariant(),
            BatchCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
            _ => text
        };
        return text + ext;
    }

    /// <summary>Renames every changed item in two phases (to temporary names, then to the final ones) so swaps and
    /// case-only renames work. On failure everything done so far is rolled back and the exception rethrown; if the
    /// rollback itself fails, a <see cref="BatchRenameRollbackException"/> names every file left under a temp name.
    /// Returns the applied (old, new) full paths; unchanged items are skipped.
    /// <paramref name="afterStep"/> is a test seam, called after each successful rename.</summary>
    public static IReadOnlyList<(string Old, string New)> Apply(IReadOnlyList<BatchRenameItem> plan, Action? afterStep = null)
    {
        var first = plan.FirstOrDefault(p => p.Error != null);
        if (first != null) throw new InvalidOperationException($"'{first.OldName}': {first.Error}");
        var work = plan.Where(p => p.NewName != p.OldName).ToList();

        var done = new List<(string From, string To)>(); // actual renames, for rollback
        var temps = new List<string>();
        try
        {
            foreach (var p in work)
            {
                string tmp = FileOperations.Rename(p.OldPath, ".frost-rename-" + Guid.NewGuid().ToString("N"));
                done.Add((p.OldPath, tmp));
                temps.Add(tmp);
                afterStep?.Invoke();
            }
            var applied = new List<(string, string)>();
            for (int i = 0; i < work.Count; i++)
            {
                string final = FileOperations.Rename(temps[i], work[i].NewName);
                done.Add((temps[i], final));
                applied.Add((work[i].OldPath, final));
                afterStep?.Invoke();
            }
            return applied;
        }
        catch (Exception cause)
        {
            var stranded = new List<string>();
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { FileOperations.Rename(done[i].To, Path.GetFileName(done[i].From)); }
                catch (Exception e)
                {
                    // Still sitting at done[i].To; if that is a temp name, tell the user how to restore it.
                    stranded.Add($"'{done[i].To}' (should be '{done[i].From}': {e.Message})");
                }
            }
            if (stranded.Count == 0) throw;
            throw new BatchRenameRollbackException(
                $"Rename failed ({cause.Message}) and could not be fully undone. These items were left under other names: "
                + string.Join("; ", stranded), cause);
        }
    }
}

/// <summary>Apply failed and the rollback failed too; the message lists each item and the name it was left under.</summary>
public sealed class BatchRenameRollbackException(string message, Exception inner) : IOException(message, inner);
