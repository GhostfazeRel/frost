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
    public static IReadOnlyList<BatchRenameItem> Plan(IReadOnlyList<string> paths, BatchRenameOptions o)
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
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException) { optionError = "Invalid regular expression."; }
        }

        var names = new string[src.Length];
        var errors = new string?[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            string name = Path.GetFileName(src[i]);
            errors[i] = optionError;
            try { names[i] = Transform(src[i], name, i, o, rx); }
            catch (RegexMatchTimeoutException) { names[i] = name; errors[i] = "Regular expression took too long."; }
            catch (RegexParseException) { names[i] = name; errors[i] ??= "Invalid replacement."; }
        }

        var sources = new HashSet<string>(src, StringComparer.OrdinalIgnoreCase);
        var targets = src.Select((p, i) => Path.Combine(Path.GetDirectoryName(p) ?? "", names[i])).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets) counts[t] = counts.GetValueOrDefault(t) + 1;

        var result = new List<BatchRenameItem>(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
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
                else if (!sources.Contains(targets[i]) && (File.Exists(targets[i]) || Directory.Exists(targets[i])))
                    err = "An item with this name already exists.";
            }
            result.Add(new BatchRenameItem(src[i], names[i], err));
        }
        return result;
    }

    private static string Transform(string path, string name, int index, BatchRenameOptions o, Regex? rx)
    {
        string ext = "";
        string text = name;
        if (!o.IncludeExtension && !Directory.Exists(path))
        {
            ext = Path.GetExtension(name);
            text = name[..^ext.Length];
        }
        if (rx != null) text = rx.Replace(text, o.UseRegex ? o.Replace : o.Replace.Replace("$", "$$"));
        text = o.Prefix + text + o.Suffix;
        long n = o.Start + (long)index * o.Step;
        text = text.Replace("{n}", n.ToString("D" + Math.Clamp(o.Width, 1, 18), CultureInfo.InvariantCulture));
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
    /// case-only renames work. On any failure everything done so far is rolled back and the exception rethrown.
    /// Returns the applied (old, new) full paths; unchanged items are skipped.</summary>
    public static IReadOnlyList<(string Old, string New)> Apply(IReadOnlyList<BatchRenameItem> plan)
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
            }
            var applied = new List<(string, string)>();
            for (int i = 0; i < work.Count; i++)
            {
                string final = FileOperations.Rename(temps[i], work[i].NewName);
                done.Add((temps[i], final));
                applied.Add((work[i].OldPath, final));
            }
            return applied;
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { FileOperations.Rename(done[i].To, Path.GetFileName(done[i].From)); } catch { /* best effort: keep rolling back the rest */ }
            }
            throw;
        }
    }
}
