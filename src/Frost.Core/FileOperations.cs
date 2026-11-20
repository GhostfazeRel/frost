using System.Buffers;
using System.Diagnostics;

namespace Frost.Core;

public sealed record OperationProgress(string CurrentItem, long BytesDone, long BytesTotal, int ItemsDone, int ItemsTotal);

public enum ConflictChoice { Replace, Skip, KeepBoth }

/// <summary>A top-level item a transfer created: where it came from and the exact path it ended up at.</summary>
public sealed record TransferredItem(string Source, string Destination);

public static class FileOperations
{
    private const int BufferSize = 1 << 20;
    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Creates "New folder" (or "New folder (2)"...) in dir; returns its full path.</summary>
    public static string CreateFolder(string directory)
    {
        string path = Path.Combine(directory, PathUtil.UniqueName(directory, "New folder"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates an empty file with a unique name; returns its full path.</summary>
    public static string CreateFile(string directory, string desiredName)
    {
        if (!PathUtil.IsValidFileName(desiredName)) throw new ArgumentException("Invalid file name.", nameof(desiredName));
        string path = Path.Combine(directory, PathUtil.UniqueName(directory, desiredName));
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        return path;
    }

    /// <summary>Renames file or folder in place; returns the new full path. Throws ArgumentException for invalid names,
    /// IOException if the target exists (a case-only rename must work).</summary>
    public static string Rename(string path, string newName)
    {
        if (!PathUtil.IsValidFileName(newName)) throw new ArgumentException("Invalid name.", nameof(newName));
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string target = Path.Combine(Path.GetDirectoryName(trimmed) ?? "", newName);
        if (string.Equals(trimmed, target, StringComparison.Ordinal)) return target;

        bool isDir = Directory.Exists(trimmed);
        if (!isDir && !File.Exists(trimmed)) throw new FileNotFoundException("Item not found.", trimmed);
        // Case-only: on Windows the "target" is the item itself, so skip the exists check and let Move do it.
        bool caseOnly = string.Equals(trimmed, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException($"'{newName}' already exists.");
        if (isDir) Directory.Move(trimmed, target); else File.Move(trimmed, target);
        return target;
    }

    /// <summary>Copies (move=false) or moves files/folders into destDir, recursively, streaming with progress
    /// (report at most ~10x/second). Copy into the same folder auto-renames ("x - Copy.txt", like Explorer).
    /// Copying a folder into itself/descendant throws InvalidOperationException. Moves on the same volume use
    /// Directory/File.Move. `onConflict` is asked per existing target. Honors cancellation; a partially written
    /// file is deleted on cancel/failure.
    /// Notes: folder-onto-folder merges silently (only file conflicts are asked); directory reparse points
    /// (junctions/symlinked folders) inside a copied folder are skipped.
    /// `completed` (optional) receives each top-level item that landed at a NEW path (after collision renaming), so it can be
    /// undone exactly; replaced/merged/skipped items are not listed. It is filled as items finish, so it is valid after a cancel.</summary>
    public static async Task TransferAsync(IReadOnlyList<string> sources, string destDir, bool move,
        Func<string, ConflictChoice> onConflict, IProgress<OperationProgress>? progress, CancellationToken ct,
        List<TransferredItem>? completed = null)
    {
        var srcs = sources.Select(Path.TrimEndingDirectorySeparator).ToArray();
        foreach (var s in srcs)
            if (Directory.Exists(s) && IsSameOrUnder(destDir, s))
                throw new InvalidOperationException($"Cannot copy or move '{s}' into itself.");

        Directory.CreateDirectory(destDir);
        var ctx = new Ctx(progress, srcs.Length);
        if (progress != null)
            ctx.Total = (await Task.Run(() => MeasureCore(srcs, ct), ct).ConfigureAwait(false)).Bytes;

        foreach (var src in srcs)
        {
            ct.ThrowIfCancellationRequested();
            bool isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src)) throw new FileNotFoundException("Item not found.", src);
            string name = Path.GetFileName(src);
            string target = Path.Combine(destDir, name);
            if (SamePath(Path.GetDirectoryName(src) ?? "", destDir))
            {
                if (move) { ctx.ItemsDone++; continue; }
                target = Path.Combine(destDir, CopyName(destDir, name, isDir));
            }
            ctx.Current = name;
            string wanted = target;
            bool existed = File.Exists(wanted) || Directory.Exists(wanted);
            string? final = isDir ? await TransferDir(src, target, move, onConflict, ctx, ct).ConfigureAwait(false)
                                  : await TransferFile(src, target, move, onConflict, ctx, ct).ConfigureAwait(false);
            if (final != null && (!existed || !SamePath(final, wanted))) completed?.Add(new TransferredItem(src, final));
            ctx.ItemsDone++;
            ctx.Report(force: false);
        }
        ctx.Done = Math.Max(ctx.Done, ctx.Total);
        ctx.Report(force: true);
    }

    /// <summary>Copies/moves each Source to exactly Destination (no renaming; an existing target is skipped). Used by undo/redo.</summary>
    public static async Task TransferExactAsync(IReadOnlyList<TransferredItem> items, bool move,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var ctx = new Ctx(progress, items.Count);
        if (progress != null)
            ctx.Total = (await Task.Run(() => MeasureCore(items.Select(i => i.Source).ToArray(), ct), ct).ConfigureAwait(false)).Bytes;
        foreach (var (src, dst) in items)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Current = Path.GetFileName(dst);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Directory.Exists(src)) await TransferDir(src, dst, move, _ => ConflictChoice.Skip, ctx, ct).ConfigureAwait(false);
            else await TransferFile(src, dst, move, _ => ConflictChoice.Skip, ctx, ct).ConfigureAwait(false);
            ctx.ItemsDone++;
            ctx.Report(force: false);
        }
        ctx.Done = Math.Max(ctx.Done, ctx.Total);
        ctx.Report(force: true);
    }

    /// <summary>Permanent delete (files and folders recursively). Recycle-bin delete lives in Frost.Shell.</summary>
    public static void DeletePermanently(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            var path = Path.TrimEndingDirectorySeparator(p);
            try { DeleteOne(path); }
            catch (UnauthorizedAccessException) { ClearReadOnly(path); DeleteOne(path); } // read-only items
        }

        static void DeleteOne(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
    }

    /// <summary>Total size and counts of the given paths (recursive), skipping unreadable parts.</summary>
    public static Task<(long Bytes, int Files, int Folders)> MeasureAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => Task.Run(() => MeasureCore(paths, ct), ct);

    // ---- internals ----

    private static (long Bytes, int Files, int Folders) MeasureCore(IReadOnlyList<string> paths, CancellationToken ct)
    {
        long bytes = 0; int files = 0, folders = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(p)) { folders++; stack.Push(p); }
            else if (File.Exists(p)) { files++; try { bytes += new FileInfo(p).Length; } catch (IOException) { } }
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (info is DirectoryInfo)
                        {
                            folders++;
                            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(info.FullName);
                        }
                        else { files++; bytes += ((FileInfo)info).Length; }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return (bytes, files, folders);
    }

    private sealed class Ctx(IProgress<OperationProgress>? progress, int itemsTotal)
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _last = long.MinValue / 2;
        public long Done, Total;
        public int ItemsDone;
        public string Current = "";

        public void Add(long n) { Done += n; Report(force: false); }

        public void Report(bool force)
        {
            if (progress == null) return;
            long now = _sw.ElapsedMilliseconds;
            if (!force && now - _last < 100) return;
            _last = now;
            progress.Report(new OperationProgress(Current, Done, Math.Max(Total, Done), ItemsDone, itemsTotal));
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathCmp);

    private static bool IsSameOrUnder(string child, string parent)
    {
        string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(c, p, PathCmp) ||
               (c.Length > p.Length && WinPath.IsSep(c[p.Length]) && c.StartsWith(p, PathCmp));
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string CopyName(string dir, string name, bool isDir)
    {
        string ext = isDir ? "" : Path.GetExtension(name);
        string stem = name[..^ext.Length];
        if (stem.Length == 0) { stem = name; ext = ""; }
        string candidate = $"{stem} - Copy{ext}";
        for (int n = 2; File.Exists(Path.Combine(dir, candidate)) || Directory.Exists(Path.Combine(dir, candidate)); n++)
            candidate = $"{stem} - Copy ({n}){ext}";
        return candidate;
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var f in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
            }
            if (File.Exists(path) || Directory.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<string?> TransferFile(string src, string target, bool move,
        Func<string, ConflictChoice> onConflict, Ctx ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long length = new FileInfo(src).Length;
        bool overwrite = false;
        if (File.Exists(target) || Directory.Exists(target))
        {
            switch (onConflict(target))
            {
                case ConflictChoice.Skip: ctx.Add(length); return null;
                case ConflictChoice.KeepBoth:
                    string dir = Path.GetDirectoryName(target)!;
                    target = Path.Combine(dir, PathUtil.UniqueName(dir, Path.GetFileName(target)));
                    break;
                default:
                    if (Directory.Exists(target)) DeletePermanently(new[] { target });
                    overwrite = true;
                    break;
            }
        }

        if (move && SameVolume(src, target))
        {
            if (overwrite) ClearReadOnly(target);
            File.Move(src, target, overwrite);
            ctx.Add(length);
            return target;
        }
        await CopyFileAsync(src, target, overwrite, ctx, ct).ConfigureAwait(false);
        if (move) { ClearReadOnly(src); File.Delete(src); }
        return target;
    }

    private static async Task CopyFileAsync(string src, string dst, bool overwrite, Ctx ctx, CancellationToken ct)
    {
        var info = new FileInfo(src);
        var attrs = info.Attributes;
        DateTime created = info.CreationTimeUtc, written = info.LastWriteTimeUtc;
        // Replacing: write beside the target and swap in at the end so a cancel never destroys the original.
        bool viaTemp = overwrite && File.Exists(dst);
        string write = viaTemp ? dst + ".frost-partial" : dst;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            try
            {
                await using (var input = new FileStream(src, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 0, Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                }))
                await using (var output = new FileStream(write, new FileStreamOptions
                {
                    Mode = viaTemp || overwrite ? FileMode.Create : FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    BufferSize = 0, Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                    PreallocationSize = info.Length,
                }))
                {
                    int n;
                    while ((n = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        ctx.Add(n);
                    }
                }
                ct.ThrowIfCancellationRequested();
                File.SetCreationTimeUtc(write, created);
                File.SetLastWriteTimeUtc(write, written);
                if (viaTemp) { ClearReadOnly(dst); File.Move(write, dst, true); }
                var keep = attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
                File.SetAttributes(dst, keep == 0 ? FileAttributes.Normal : keep);
            }
            catch
            {
                try { File.Delete(write); } catch (Exception) { }
                throw;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task<string?> TransferDir(string src, string target, bool move,
        Func<string, ConflictChoice> onConflict, Ctx ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(target))
        {
            switch (onConflict(target))
            {
                case ConflictChoice.Skip: return null;
                case ConflictChoice.KeepBoth:
                    string dir = Path.GetDirectoryName(target)!;
                    target = Path.Combine(dir, PathUtil.UniqueName(dir, Path.GetFileName(target)));
                    break;
                default: DeletePermanently(new[] { target }); break;
            }
        }

        if (move && !Directory.Exists(target) && SameVolume(src, target))
        {
            Directory.Move(src, target);
            return target;
        }

        var existed = Directory.Exists(target);
        Directory.CreateDirectory(target);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(src).EnumerateFileSystemInfos("*", options).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            string t = Path.Combine(target, info.Name);
            if (info is DirectoryInfo)
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                await TransferDir(info.FullName, t, move, onConflict, ctx, ct).ConfigureAwait(false);
            }
            else
            {
                ctx.Current = info.Name;
                await TransferFile(info.FullName, t, move, onConflict, ctx, ct).ConfigureAwait(false);
            }
        }

        if (!existed)
        {
            try
            {
                var di = new DirectoryInfo(src);
                Directory.SetCreationTimeUtc(target, di.CreationTimeUtc);
                Directory.SetLastWriteTimeUtc(target, di.LastWriteTimeUtc);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (move)
        {
            try { if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return target;
    }
}
