using System.Diagnostics;
using System.Security.Cryptography;

namespace Frost.Core;

/// <summary>Folder walking shared by the space analyzer, biggest files and duplicates, plus the edit-backup store.</summary>
internal static class Scans
{
    // ---- walking rules ----
    private static HashSet<string> MountPoints()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsLinux()) return set; // Windows: junctions/reparse points cover other volumes
        try
        {
            foreach (var l in File.ReadLines("/proc/mounts"))
                if (l.Split(' ') is { Length: > 1 } f)
                    set.Add(f[1].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\"));
        }
        catch (Exception) { }
        return set;
    }

    private static string FullRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) is { Length: > 0 } r ? r : Path.GetFullPath(root);

    /// <summary>Entries of a folder, or null when it can't be read.</summary>
    private static List<FileSystemInfo>? List(string dir)
    {
        try
        {
            var o = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", o).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsLink(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) != 0;
    private static long Len(FileSystemInfo i) { try { return ((FileInfo)i).Length; } catch (Exception) { return 0; } }

    public static bool IsExcluded(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] == '/')
        {
            static bool Under(string p, string pre) => p == pre || p.StartsWith(pre + "/", StringComparison.Ordinal);
            string p = path.Length > 1 ? path.TrimEnd('/') : path;
            if (Under(p, "/proc") || Under(p, "/sys") || Under(p, "/dev") || Under(p, "/snap")) return true;
            if (Under(p, "/run") && !Under(p, "/run/media")) return true;
        }
        foreach (var seg in path.Split('/', '\\'))
            if (seg.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || seg.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- space analyzer ----
    public static Task<SizeNode> Space(string root, IProgress<OperationProgress>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            string r = FullRoot(root);
            var mounts = MountPoints();
            var sw = Stopwatch.StartNew();
            long last = 0, bytes = 0; int items = 0;
            string name = Path.GetFileName(r) is { Length: > 0 } nm ? nm : r;
            var node = Dir(r, name);
            progress?.Report(new OperationProgress(r, bytes, 0, items, 0));
            return node;

            SizeNode Dir(string path, string nm)
            {
                ct.ThrowIfCancellationRequested();
                var list = List(path);
                if (list == null) return new SizeNode(path, nm, true, 0, Array.Empty<SizeNode>(), true);
                var kids = new List<SizeNode>();
                long total = 0;
                foreach (var e in list)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsLink(e)) continue;
                    SizeNode child;
                    if (e is DirectoryInfo)
                    {
                        if (mounts.Contains(e.FullName)) continue;
                        child = Dir(e.FullName, e.Name);
                    }
                    else
                    {
                        child = new SizeNode(e.FullName, e.Name, false, Len(e), Array.Empty<SizeNode>(), false);
                        items++; bytes += child.Size;
                        if (sw.ElapsedMilliseconds - last >= 100) { last = sw.ElapsedMilliseconds; progress?.Report(new OperationProgress(path, bytes, 0, items, 0)); }
                    }
                    kids.Add(child); total += child.Size;
                }
                kids.Sort((a, b) => b.Size != a.Size ? b.Size.CompareTo(a.Size) : string.CompareOrdinal(a.Name, b.Name));
                return new SizeNode(path, nm, true, total, kids, false);
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // ---- biggest files ----
    public static async Task<IReadOnlyList<DriveScan>> Biggest(IReadOnlyList<string> roots, int topN, IProgress<DriveScan>? progress, CancellationToken ct)
    {
        var tasks = roots.Select(r => Task.Factory.StartNew(() => BigRoot(r, topN, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        return await Task.WhenAll(tasks);
    }

    private static DriveScan BigRoot(string rootArg, int topN, IProgress<DriveScan>? progress, CancellationToken ct)
    {
        const int MaxUnreadable = 1000;
        var heap = new PriorityQueue<BigFile, long>(); // min-heap: the smallest of the current top N is on top
        var unread = new List<string>();
        long files = 0, last = 0;
        var sw = Stopwatch.StartNew();
        DriveScan Snap(bool done) => new(rootArg,
            heap.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ThenBy(f => f.Path, StringComparer.Ordinal).ToList(),
            files, unread.ToList(), done);

        var stack = new Stack<string>();
        string root = FullRoot(rootArg);
        if (Directory.Exists(root)) stack.Push(root); else unread.Add(rootArg);
        var mounts = MountPoints();
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var list = List(dir);
            if (list == null) { if (unread.Count < MaxUnreadable) unread.Add(dir); continue; }
            foreach (var e in list)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsLink(e) && !IsExcluded(e.FullName))
                {
                    if (e is DirectoryInfo) { if (!mounts.Contains(e.FullName)) stack.Push(e.FullName); }
                    else
                    {
                        files++;
                        long size = Len(e);
                        if (topN > 0 && (heap.Count < topN || size > heap.Peek().Size))
                        {
                            if (heap.Count >= topN) heap.Dequeue();
                            DateTime m; try { m = e.LastWriteTime; } catch (Exception) { m = default; }
                            heap.Enqueue(new BigFile(e.FullName, size, m), size);
                        }
                    }
                }
                if (progress != null && sw.ElapsedMilliseconds - last >= 250) { last = sw.ElapsedMilliseconds; progress.Report(Snap(false)); }
            }
        }
        var final = Snap(true);
        progress?.Report(final);
        return final;
    }

    // ---- duplicates ----
    public static Task<IReadOnlyList<DuplicateGroup>> Dupes(string root, IProgress<OperationProgress>? progress, CancellationToken ct) =>
        Task.Factory.StartNew<IReadOnlyList<DuplicateGroup>>(() =>
        {
            const int Partial = 64 * 1024;
            var bySize = new Dictionary<long, List<string>>();
            var stack = new Stack<string>();
            string r = FullRoot(root);
            stack.Push(r);
            var mounts = MountPoints();
            var sw = Stopwatch.StartNew();
            long last = 0, hashed = 0;
            int seen = 0, done = 0, total = 0;
            void Tick(string cur, bool force = false)
            {
                if (progress == null || (!force && sw.ElapsedMilliseconds - last < 100)) return;
                last = sw.ElapsedMilliseconds;
                progress.Report(new OperationProgress(cur, hashed, 0, total == 0 ? seen : done, total));
            }

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                var list = List(dir);
                if (list == null) continue;
                foreach (var e in list)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsLink(e)) continue;
                    if (e is DirectoryInfo) { if (!mounts.Contains(e.FullName)) stack.Push(e.FullName); continue; }
                    seen++;
                    long len = Len(e);
                    if (len > 0) { if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = new(); l.Add(e.FullName); }
                    Tick(dir);
                }
            }

            total = bySize.Values.Where(l => l.Count > 1).Sum(l => l.Count);
            var groups = new List<DuplicateGroup>();
            foreach (var (size, paths) in bySize)
            {
                if (paths.Count < 2) continue;
                foreach (var part in paths.Select(p => (Path: p, Hash: HashOf(p, Partial))).Where(x => x.Hash != null).GroupBy(x => x.Hash!))
                {
                    done += part.Count();
                    var members = part.Select(x => x.Path).ToList();
                    if (members.Count < 2) continue;
                    if (size > Partial)
                    {
                        total += members.Count;
                        var full = members.Select(p => (Path: p, Hash: HashOf(p, long.MaxValue))).Where(x => x.Hash != null).ToList();
                        done += members.Count;
                        foreach (var g in full.GroupBy(x => x.Hash!).Where(g => g.Count() > 1))
                            groups.Add(new DuplicateGroup(size, g.Select(x => x.Path).OrderBy(p => p, StringComparer.Ordinal).ToList()));
                    }
                    else groups.Add(new DuplicateGroup(size, members.OrderBy(p => p, StringComparer.Ordinal).ToList()));
                }
            }
            Tick(r, force: true);
            return groups.OrderByDescending(g => (g.Paths.Count - 1) * g.Size).ThenBy(g => g.Paths[0], StringComparer.Ordinal).ToList();

            string? HashOf(string path, long limit)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buf = new byte[1 << 16];
                    long left = limit;
                    int n;
                    while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        h.AppendData(buf, 0, n);
                        left -= n; hashed += n;
                        Tick(path);
                    }
                    return Convert.ToHexString(h.GetHashAndReset());
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // ---- edit backups ----
    public static string BackupFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frost", "undo")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "frost", "undo");

    public static string Backup(string path)
    {
        string dir = BackupFolder;
        Directory.CreateDirectory(dir);
        try
        {
            // the name starts with the creation time (File.Copy keeps the original's mtime, so that can't be used)
            string cutoff = DateTime.UtcNow.AddDays(-7).ToString("yyyyMMddHHmmss");
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                if (f.Name.Length > 15 && f.Name[14] == '-' && f.Name.AsSpan(0, 14).ContainsAnyExceptInRange('0', '9') == false
                    && string.CompareOrdinal(f.Name[..14], cutoff) < 0) try { f.Delete(); } catch (Exception) { }
        }
        catch (Exception) { }
        string dest = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(path));
        File.Copy(path, dest);
        return dest;
    }
}
