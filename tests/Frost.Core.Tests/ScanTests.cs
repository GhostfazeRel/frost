using Frost.Core;

namespace Frost.Core.Tests;

public sealed class ScanTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("frost-scan").FullName;
    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (var x in Directory.EnumerateDirectories(_d, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(x, (UnixFileMode)0b111_101_101);
            Directory.Delete(_d, true);
        }
        catch { }
    }

    private string F(string rel, int size, byte fill = 1)
    {
        var p = Path.Combine(_d, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Enumerable.Repeat(fill, size).ToArray());
        return p;
    }

    private static bool CanTestPermissions => !OperatingSystem.IsWindows() && Environment.UserName != "root" && geteuid() != 0;
    [System.Runtime.InteropServices.DllImport("libc")] private static extern uint geteuid();

    // ---- space ----
    [Fact]
    public async Task Space_sizes_and_sorting()
    {
        F("a/big.bin", 5000); F("a/small.bin", 10); F("b/c/mid.bin", 700); F("top.bin", 100);
        var root = await SpaceScan.ScanAsync(_d, null, default);
        Assert.Equal(5810, root.Size);
        Assert.True(root.IsDirectory && !root.Unreadable);
        Assert.Equal(new[] { "a", "b", "top.bin" }, root.Children.Select(c => c.Name));
        var a = root.Children[0];
        Assert.Equal(5010, a.Size);
        Assert.Equal(new[] { "big.bin", "small.bin" }, a.Children.Select(c => c.Name));
        Assert.Equal(Path.Combine(_d, "b", "c"), root.Children[1].Children[0].Path);
    }

    [Fact]
    public async Task Space_skips_symlink_loops()
    {
        if (OperatingSystem.IsWindows()) return;
        F("x/f.bin", 100);
        Directory.CreateSymbolicLink(Path.Combine(_d, "x", "loop"), _d);
        File.CreateSymbolicLink(Path.Combine(_d, "x", "link.bin"), Path.Combine(_d, "x", "f.bin"));
        var root = await SpaceScan.ScanAsync(_d, null, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100, root.Size);
        Assert.Single(root.Children[0].Children);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Space_marks_unreadable_folder()
    {
        if (!CanTestPermissions) return;
        F("ok/f.bin", 50); F("locked/g.bin", 50);
        File.SetUnixFileMode(Path.Combine(_d, "locked"), UnixFileMode.None);
        var root = await SpaceScan.ScanAsync(_d, null, default);
        var locked = root.Children.Single(c => c.Name == "locked");
        Assert.True(locked.Unreadable);
        Assert.Equal(0, locked.Size);
        Assert.Equal(50, root.Size);
        Assert.False(root.Children.Single(c => c.Name == "ok").Unreadable);
    }

    [Fact]
    public async Task Space_cancels_and_reports_progress()
    {
        for (int i = 0; i < 50; i++) F($"d{i}/f.bin", 10);
        var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpaceScan.ScanAsync(_d, null, cts.Token));
        var reports = new List<OperationProgress>();
        await SpaceScan.ScanAsync(_d, new SyncProgress<OperationProgress>(reports.Add), default);
        Assert.NotEmpty(reports);
        Assert.Equal(50, reports[^1].ItemsDone);
        Assert.Equal(500, reports[^1].BytesDone);
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) { lock (this) a(v); } }

    // ---- biggest ----
    [Fact]
    public async Task Biggest_top_n_and_ties()
    {
        F("a/1.bin", 100); F("a/2.bin", 300); F("b/3.bin", 200); F("b/c/4.bin", 300); F("5.bin", 50);
        var r = await BiggestFiles.ScanAsync(new[] { _d }, 3, null, default);
        var d = Assert.Single(r);
        Assert.True(d.Done);
        Assert.Equal(5, d.FilesScanned);
        Assert.Equal(new long[] { 300, 300, 200 }, d.Top.Select(t => t.Size));
        Assert.Equal(_d, d.Root);
        Assert.Empty(d.Unreadable);
        Assert.True((DateTime.Now - d.Top[0].Modified).TotalMinutes < 5);
    }

    [Fact]
    public async Task Biggest_multiple_roots_and_combine_and_progress()
    {
        F("one/a.bin", 10); F("one/b.bin", 90); F("two/c.bin", 50); F("two/d.bin", 70);
        var seen = new List<DriveScan>();
        var r = await BiggestFiles.ScanAsync(new[] { Path.Combine(_d, "one"), Path.Combine(_d, "two") }, 2, new SyncProgress<DriveScan>(seen.Add), default);
        Assert.Equal(2, r.Count);
        Assert.Equal(new long[] { 90, 10 }, r[0].Top.Select(t => t.Size));
        Assert.Equal(2, seen.Count(s => s.Done));
        Assert.Equal(new long[] { 90, 70, 50 }, BiggestFiles.Combine(r, 3).Select(t => t.Size));
        Assert.Empty(BiggestFiles.Combine(r, 0));
        var missing = (await BiggestFiles.ScanAsync(new[] { Path.Combine(_d, "nope") }, 5, null, default))[0];
        Assert.True(missing.Done); Assert.Single(missing.Unreadable);
    }

    [Fact]
    public async Task Biggest_keeps_only_top_n_in_memory_and_handles_many_files()
    {
        for (int i = 0; i < 300; i++) F($"m/f{i}.bin", i + 1);
        var d = (await BiggestFiles.ScanAsync(new[] { _d }, 5, null, default))[0];
        Assert.Equal(new long[] { 300, 299, 298, 297, 296 }, d.Top.Select(t => t.Size));
        Assert.Equal(300, d.FilesScanned);
    }

    [Fact]
    public async Task Biggest_skips_symlinks_and_reports_unreadable()
    {
        F("real/f.bin", 100);
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_d, "real", "loop"), _d);
        File.CreateSymbolicLink(Path.Combine(_d, "alias.bin"), Path.Combine(_d, "real", "f.bin"));
        var d = (await BiggestFiles.ScanAsync(new[] { _d }, 10, null, default).WaitAsync(TimeSpan.FromSeconds(10)))[0];
        Assert.Single(d.Top);
        if (CanTestPermissions)
        {
            F("locked/g.bin", 5);
            File.SetUnixFileMode(Path.Combine(_d, "locked"), UnixFileMode.None);
            d = (await BiggestFiles.ScanAsync(new[] { _d }, 10, null, default))[0];
            Assert.Equal(new[] { Path.Combine(_d, "locked") }, d.Unreadable);
            Assert.Single(d.Top);
        }
    }

    [Fact]
    public async Task Biggest_cancels_promptly()
    {
        for (int i = 0; i < 200; i++) F($"c{i % 10}/f{i}.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BiggestFiles.ScanAsync(new[] { _d }, 5, null, cts.Token));
    }

    [Fact]
    public void Excluded_paths()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in new[] { "/proc", "/proc/1/status", "/sys/kernel", "/dev/null", "/run", "/run/user/1000", "/snap/core", "/mnt/x/$Recycle.Bin/a", "/mnt/x/System Volume Information" })
                Assert.True(BiggestFiles.IsExcluded(p), p);
            foreach (var p in new[] { "/", "/home/u", "/run/media", "/run/media/u/disk/a.bin", "/mnt/gry", "/processing", "/devices" })
                Assert.False(BiggestFiles.IsExcluded(p), p);
        }
        Assert.True(BiggestFiles.IsExcluded(@"C:\$Recycle.Bin\S-1\x"));
        Assert.True(BiggestFiles.IsExcluded(@"D:\System Volume Information"));
        Assert.False(BiggestFiles.IsExcluded(@"C:\Users\me"));
    }

    // ---- duplicates ----
    [Fact]
    public async Task Duplicates_group_by_content_not_just_size()
    {
        F("a.bin", 200_000, 1); F("sub/a-copy.bin", 200_000, 1);               // identical big
        F("same-size-diff.bin", 200_000, 2);                                    // same size, different content from byte 0
        var late = Enumerable.Repeat((byte)1, 200_000).ToArray(); late[150_000] = 9; // same first 64 KB, differs later
        File.WriteAllBytes(Path.Combine(_d, "late-diff.bin"), late);
        F("s1.txt", 10, 5); F("s2.txt", 10, 5); F("s3.txt", 10, 5);             // small trio
        F("e1.bin", 0); F("e2.bin", 0);                                         // empty ignored
        F("u.bin", 77);                                                         // unique
        var g = await Duplicates.FindAsync(_d, null, default);
        Assert.Equal(2, g.Count);
        Assert.Equal(200_000, g[0].Size);
        Assert.Equal(new[] { Path.Combine(_d, "a.bin"), Path.Combine(_d, "sub", "a-copy.bin") }, g[0].Paths);
        Assert.Equal(3, g[1].Paths.Count);
    }

    [Fact]
    public async Task Duplicates_cancel_and_symlinks()
    {
        F("a/x.bin", 1000, 3); F("b/y.bin", 1000, 3);
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(_d, "link.bin"), Path.Combine(_d, "a", "x.bin"));
        var g = await Duplicates.FindAsync(_d, null, default);
        Assert.Equal(2, Assert.Single(g).Paths.Count);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Duplicates.FindAsync(_d, null, cts.Token));
    }
}
