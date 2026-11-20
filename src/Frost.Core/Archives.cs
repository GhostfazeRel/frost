using System.Diagnostics;
using System.IO.Compression;

namespace Frost.Core;

public static class Archives
{
    private const int BufferSize = 1 << 17;

    public static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Unique zip path in dir: one item uses its own name, several use the folder's name.</summary>
    public static string ZipPathFor(IReadOnlyList<string> paths, string dir)
    {
        string baseName = paths.Count == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) : Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        if (paths.Count == 1 && File.Exists(paths[0])) baseName = Path.GetFileNameWithoutExtension(baseName);
        if (baseName.Length == 0) baseName = "Archive";
        return Path.Combine(dir, PathUtil.UniqueName(dir, baseName + ".zip"));
    }

    /// <summary>Unique, not yet existing folder in parent named after the zip (without .zip).</summary>
    public static string ExtractFolderFor(string zipPath, string parent)
        => Path.Combine(parent, PathUtil.UniqueName(parent, Path.GetFileNameWithoutExtension(zipPath)));

    /// <summary>Creates destZipPath (must not exist) from files and folders (recursively, empty folders kept).
    /// Progress in bytes. Cancel or failure deletes the partial zip. Directory reparse points (junctions/symlinked folders) are skipped to avoid loops and and dangling links are skipped; the count is returned.
    /// File reparse points (symlinks, cloud placeholders) are read like normal files.</summary>
    public static async Task<int> CompressAsync(IReadOnlyList<string> paths, string destZipPath,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var srcs = paths.Select(Path.TrimEndingDirectorySeparator).ToArray();
        // (full path, entry name, isDir)
        var items = new List<(string Full, string Entry, bool Dir)>();
        string destFull = Path.GetFullPath(destZipPath);
        long total = 0; int skipped = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var s in srcs)
        {
            string name = Path.GetFileName(s);
            if (Directory.Exists(s))
            {
                items.Add((s, name + "/", true));
                var stack = new Stack<string>();
                stack.Push(s);
                while (stack.Count > 0) // manual walk: never descend into directory reparse points
                    foreach (var f in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos("*", opts))
                    {
                        ct.ThrowIfCancellationRequested();
                        string rel = name + "/" + Path.GetRelativePath(s, f.FullName).Replace('\\', '/');
                        if (f is DirectoryInfo)
                        {
                            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                            items.Add((f.FullName, rel + "/", true)); stack.Push(f.FullName);
                        }
                        else if (!string.Equals(f.FullName, destFull, StringComparison.OrdinalIgnoreCase)) { items.Add((f.FullName, rel, false)); try { total += ((FileInfo)f).Length; } catch (IOException) { } }
                    }
            }
            else if (File.Exists(s)) { items.Add((s, name, false)); total += new FileInfo(s).Length; }
            else if (new FileInfo(s).LinkTarget != null) skipped++; // dangling top-level link
            else throw new FileNotFoundException("Item not found.", s);
        }

        long done = 0; int count = 0, files = items.Count(i => !i.Dir);
        var sw = Stopwatch.StartNew(); long last = 0;
        void Report(string cur, bool force)
        {
            if (progress == null || (!force && sw.ElapsedMilliseconds - last < 100)) return;
            last = sw.ElapsedMilliseconds;
            progress.Report(new OperationProgress(cur, done, total, count, files));
        }

        bool created = false; // only delete what this call created (CreateNew fails if the file exists)
        try
        {
            using (var fs = new FileStream(destZipPath, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                created = true;
                var buf = new byte[BufferSize];
                foreach (var (full, entryName, isDir) in items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (isDir)
                    {
                        var d = zip.CreateEntry(entryName);
                        try { d.LastWriteTime = Directory.GetLastWriteTime(full); } catch { }
                        continue;
                    }
                    FileStream src;
                    try { src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, true); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { skipped++; continue; } // dangling link
                    using var _ = src;
                    var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    try { e.LastWriteTime = File.GetLastWriteTime(full); } catch { }
                    using var dst = e.Open();
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n; Report(Path.GetFileName(full), false);
                    }
                    count++;
                }
            }
            Report("", true);
            return skipped;
        }
        catch { if (created) TryDelete(destZipPath); throw; }
    }

    /// <summary>Extracts into destDir, which must not exist yet (refuses otherwise, never merges). Any entry that would land
    /// outside destDir throws InvalidDataException before anything is written. Extracts into a hidden temp folder next to destDir
    /// and moves it into place (next free name if destDir appeared meanwhile); returns the final path. Cancel or failure deletes only the temp folder.</summary>
    public static async Task<string> ExtractAsync(string zipPath, string destDir,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (Directory.Exists(destDir) || File.Exists(destDir)) throw new IOException($"'{Path.GetFileName(destDir)}' already exists.");
        string final = Path.GetFullPath(destDir);
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(final))!;
        string root = Path.Combine(parent, ".frost-extract-" + Guid.NewGuid().ToString("N"));
        string rootSep = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);

        var targets = new List<(ZipArchiveEntry E, string Path)>();
        long total = 0;
        foreach (var e in zip.Entries)
        {
            string full = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('\\', '/')));
            if (!full.StartsWith(rootSep, PathCmp) && !string.Equals(full, root, PathCmp))
                throw new InvalidDataException($"Unsafe path in archive: \"{e.FullName}\".");
            targets.Add((e, full)); total += e.Length;
        }

        long done = 0; int count = 0, files = targets.Count(t => !t.E.FullName.EndsWith('/') && !t.E.FullName.EndsWith('\\'));
        var sw = Stopwatch.StartNew(); long last = 0;
        bool created = false; // root is a fresh GUID name, so it is always ours
        try
        {
            Directory.CreateDirectory(root); created = true;
            try { File.SetAttributes(root, FileAttributes.Hidden | FileAttributes.Directory); } catch { } // keep it out of listings/watchers
            var buf = new byte[BufferSize];
            foreach (var (e, full) in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(full); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using (var src = e.Open())
                using (var dst = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, true))
                {
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (progress != null && sw.ElapsedMilliseconds - last >= 100)
                        { last = sw.ElapsedMilliseconds; progress.Report(new OperationProgress(e.Name, done, total, count, files)); }
                    }
                }
                try { File.SetLastWriteTime(full, e.LastWriteTime.LocalDateTime); } catch { }
                count++;
            }
            progress?.Report(new OperationProgress("", total, total, count, files));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Move(root, final); created = false;
                    try { File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden); } catch { }
                    return final;
                }
                catch (IOException) when (attempt < 5 && (Directory.Exists(final) || File.Exists(final)))
                { final = Path.Combine(parent, PathUtil.UniqueName(parent, Path.GetFileName(final))); }
            }
        }
        catch
        {
            try { if (created && Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            throw;
        }
    }

    /// <summary>Entry count, total uncompressed bytes and the first maxNames entry names of a zip.</summary>
    public static (int Count, long Bytes, List<string> Names) Describe(string zipPath, int maxNames)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = new List<string>();
        long bytes = 0;
        foreach (var e in zip.Entries) { bytes += e.Length; if (names.Count < maxNames) names.Add(e.FullName); }
        return (zip.Entries.Count, bytes, names);
    }

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
