using System.Globalization;
using Frost.Core;

namespace Frost.Core.Tests;

public class PureTests
{
    private static int Sign(int x) => Math.Sign(x);
    private static readonly NaturalStringComparer C = NaturalStringComparer.Instance;

    [Theory]
    [InlineData("file2", "file10", -1)]
    [InlineData("a", "B", -1)]
    [InlineData("B", "a", 1)]
    [InlineData("a", "A", 0)]
    [InlineData("file", "file1", -1)]
    [InlineData("a01", "a1", 1)]
    [InlineData("a001b", "a1c", -1)]
    [InlineData("a2", "a10", -1)]
    [InlineData("12345678901234567890123", "12345678901234567890124", -1)]
    [InlineData("x99999999999999999999999999", "x100000000000000000000000000", -1)]
    [InlineData("a", "a b", -1)]
    public void Natural(string a, string b, int expected)
    {
        Assert.Equal(expected, Sign(C.Compare(a, b)));
        Assert.Equal(-expected, Sign(C.Compare(b, a)));
    }

    [Theory]
    [InlineData("C:\"", "C:\\")]
    [InlineData("d:", "d:\\")]
    [InlineData(@"C:\Users\Bob", @"C:\Users\Bob")]
    [InlineData("::{645FF040-5081-101B-9F08-00AA002F954E}", "::{645FF040-5081-101B-9F08-00AA002F954E}")]
    public void FromShellArg(string arg, string expected) => Assert.Equal(expected, PathUtil.FromShellArg(arg));

    [Fact]
    public void NaturalNulls()
    {
        Assert.Equal(0, C.Compare(null, null));
        Assert.True(C.Compare(null, "a") < 0);
        Assert.True(C.Compare("a", null) > 0);
    }

    private static FileEntry E(string name, bool dir = false, long size = 0, int day = 1) =>
        new(name, "/x/" + name, dir, size, new DateTime(2024, 1, day), new DateTime(2024, 1, 1), dir ? FileAttributes.Directory : FileAttributes.Normal);

    [Fact]
    public void SorterFoldersFirstAndName()
    {
        var items = new[] { E("b.txt"), E("zdir", true), E("a10.txt"), E("a2.txt"), E("adir", true) };
        Assert.Equal(new[] { "adir", "zdir", "a2.txt", "a10.txt", "b.txt" }, Sorter.Sort(items, SortColumn.Name, false).Select(e => e.Name));
        Assert.Equal(new[] { "zdir", "adir", "b.txt", "a10.txt", "a2.txt" }, Sorter.Sort(items, SortColumn.Name, true).Select(e => e.Name));
    }

    [Fact]
    public void SorterColumns()
    {
        var items = new[] { E("c.png", size: 5, day: 3), E("a.txt", size: 50, day: 1), E("b.bmp", size: 5, day: 2), E("d", true, day: 9) };
        Assert.Equal(new[] { "d", "b.bmp", "c.png", "a.txt" }, Sorter.Sort(items, SortColumn.Size, false).Select(e => e.Name)); // size tie -> name
        Assert.Equal(new[] { "d", "a.txt", "b.bmp", "c.png" }, Sorter.Sort(items, SortColumn.Modified, false).Select(e => e.Name));
        Assert.Equal(new[] { "d", "c.png", "b.bmp", "a.txt" }, Sorter.Sort(items, SortColumn.Modified, true).Select(e => e.Name));
        Assert.Equal(new[] { "d", "b.bmp", "c.png", "a.txt" }, Sorter.Sort(items, SortColumn.Type, false).Select(e => e.Name)); // BMP, PNG, TXT
        Assert.Equal(new[] { "d", "a.txt", "c.png", "b.bmp" }, Sorter.Sort(items, SortColumn.Type, true).Select(e => e.Name));
    }

    [Fact]
    public void FormatSize()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("0 bytes", Format.Size(0));
            Assert.Equal("1 byte", Format.Size(1));
            Assert.Equal("512 bytes", Format.Size(512));
            Assert.Equal("1 KB", Format.Size(1024));
            Assert.Equal("1.5 KB", Format.Size(1536));
            Assert.Equal("12.3 MB", Format.Size((long)(12.3 * 1024 * 1024)));
            Assert.Equal("1.02 GB", Format.Size((long)(1.02 * 1024 * 1024 * 1024)));
            Assert.Equal("123 MB", Format.Size(123L * 1024 * 1024));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void FormatDate()
    {
        var now = new DateTime(2024, 5, 10, 15, 0, 0);
        Assert.Equal("Just now", Format.Date(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", Format.Date(now.AddMinutes(-5), now));
        Assert.Equal("Today, 12:05", Format.Date(new DateTime(2024, 5, 10, 12, 5, 0), now));
        Assert.Equal("Yesterday, 09:10", Format.Date(new DateTime(2024, 5, 9, 9, 10, 0), now));
        Assert.Equal(new DateTime(2024, 5, 1, 9, 10, 0).ToString("g", CultureInfo.CurrentCulture), Format.Date(new DateTime(2024, 5, 1, 9, 10, 0), now));
    }

    [Fact]
    public void FormatTypeName()
    {
        Assert.Equal("Folder", Format.TypeName(E("x", true)));
        Assert.Equal("PNG file", Format.TypeName(E("a.png")));
        Assert.Equal("File", Format.TypeName(E("noext")));
    }

    [Fact]
    public void History()
    {
        var h = new NavigationHistory(@"C:\");
        Assert.False(h.CanGoBack);
        h.Navigate(@"C:\Users");
        h.Navigate(@"c:\users\");      // dup, ignoring case + trailing slash
        h.Navigate(@"C:\Users\Bob");
        Assert.True(h.CanGoBack);
        Assert.Equal(@"C:\Users", h.Back());
        Assert.True(h.CanGoForward);
        Assert.Equal(@"C:\Users\Bob", h.Forward());
        h.Back(); h.Back();
        Assert.Equal(@"C:\", h.Current);
        h.Navigate(@"D:\");            // clears forward
        Assert.False(h.CanGoForward);
        Assert.Equal(@"C:\", h.Back());
        Assert.False(h.CanGoBack);
    }

    [Theory]
    [InlineData(@"C:\", null)]
    [InlineData(@"C:", null)]
    [InlineData(@"\\server\share", null)]
    [InlineData(@"\\server\share\", null)]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData(@"C:\Users\Bob\", @"C:\Users")]
    [InlineData(@"\\server\share\dir", @"\\server\share")]
    [InlineData("C:/Users/Bob", @"C:\Users")]
    public void HistoryParent(string cur, string? parent) => Assert.Equal(parent, new NavigationHistory(cur).Parent());

    [Fact]
    public void Segments()
    {
        Assert.Equal(new[] { (@"C:\", @"C:\"), ("Users", @"C:\Users"), ("Bob", @"C:\Users\Bob") }, PathUtil.Segments(@"C:\Users\Bob"));
        Assert.Equal(new[] { (@"C:\", @"C:\") }, PathUtil.Segments(@"C:\"));
        Assert.Equal(new[] { (@"C:\", @"C:\"), ("Users", @"C:\Users") }, PathUtil.Segments("C:/Users/"));
        Assert.Equal(new[] { (@"\\srv\sh", @"\\srv\sh"), ("d", @"\\srv\sh\d"), ("e", @"\\srv\sh\d\e") }, PathUtil.Segments(@"\\srv\sh\d\e\"));
        Assert.Empty(PathUtil.Segments(""));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("CON", false)]
    [InlineData("con.txt", false)]
    [InlineData("nul", false)]
    [InlineData("COM1", false)]
    [InlineData("console", true)]
    [InlineData("a.", false)]
    [InlineData("a ", false)]
    [InlineData("a<b", false)]
    [InlineData("a:b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData(".gitignore", true)]
    public void ValidNames(string n, bool ok) => Assert.Equal(ok, PathUtil.IsValidFileName(n));

    [Theory]
    [InlineData("report.txt", "REPORT.TXT", true)]
    [InlineData("report.txt", "*.txt", true)]
    [InlineData("report.txt", "*.png", false)]
    [InlineData("report.txt", "port", true)]
    [InlineData("report.txt", "rep?rt.*", true)]
    [InlineData("report.txt", "xyz", false)]
    [InlineData("report.txt", "", true)]
    public void SearchMatches(string name, string q, bool exp) => Assert.Equal(exp, Search.Matches(name, q));
}

public class TempFixture : IDisposable
{
    protected readonly string Root = Directory.CreateTempSubdirectory("frost-test-").FullName;
    protected string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
    protected string Write(string rel, string content = "x")
    {
        var p = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class FsTests : TempFixture
{
    [Fact]
    public void UniqueNameFilesAndFolders()
    {
        Write("a.txt"); Write("a (2).txt"); Directory.CreateDirectory(P("New folder")); Write(".git");
        Assert.Equal("a (3).txt", PathUtil.UniqueName(Root, "a.txt"));
        Assert.Equal("b.txt", PathUtil.UniqueName(Root, "b.txt"));
        Assert.Equal("New folder (2)", PathUtil.UniqueName(Root, "New folder"));
        Assert.Equal(".git (2)", PathUtil.UniqueName(Root, ".git"));
    }

    [Fact]
    public async Task ListerBasicsAndHidden()
    {
        Write("f.txt", "12345"); Directory.CreateDirectory(P("sub"));
        var hidden = Write("h.txt");
        bool canHide = false;
        try { File.SetAttributes(hidden, FileAttributes.Hidden); canHide = (File.GetAttributes(hidden) & FileAttributes.Hidden) != 0; } catch { }

        var shown = DirectoryLister.List(Root, true);
        Assert.Equal(3, shown.Count);
        var f = shown.Single(e => e.Name == "f.txt");
        Assert.Equal(5, f.Size); Assert.False(f.IsDirectory); Assert.Equal(".txt", f.Extension); Assert.Equal(P("f.txt"), f.FullPath);
        Assert.True(shown.Single(e => e.Name == "sub").IsDirectory);
        if (canHide)
        {
            Assert.Equal(2, DirectoryLister.List(Root, false).Count);
            Assert.True(shown.Single(e => e.Name == "h.txt").IsHidden);
        }
        Assert.Equal(3, (await DirectoryLister.ListAsync(Root, true)).Count);
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryLister.List(P("nope"), true));
    }

    [Fact]
    public void CreateRename()
    {
        var f1 = FileOperations.CreateFolder(Root);
        var f2 = FileOperations.CreateFolder(Root);
        Assert.Equal(P("New folder"), f1); Assert.Equal(P("New folder (2)"), f2);
        var a = FileOperations.CreateFile(Root, "n.txt");
        var b = FileOperations.CreateFile(Root, "n.txt");
        Assert.Equal(P("n.txt"), a); Assert.Equal(P("n (2).txt"), b);
        Assert.Throws<ArgumentException>(() => FileOperations.CreateFile(Root, "a<b"));

        var r = FileOperations.Rename(a, "m.txt");
        Assert.True(File.Exists(r) && !File.Exists(a));
        Assert.Throws<IOException>(() => FileOperations.Rename(r, "n (2).txt"));
        Assert.Throws<ArgumentException>(() => FileOperations.Rename(r, "bad."));
        var up = FileOperations.Rename(r, "M.TXT"); // case-only
        Assert.Equal(P("M.TXT"), up);
        Assert.Contains(Directory.GetFiles(Root), x => Path.GetFileName(x) == "M.TXT");
        var d = FileOperations.Rename(f1, "Docs");
        Assert.True(Directory.Exists(d));
    }

    [Fact]
    public async Task CopyAndMove()
    {
        var src = Write("src/a.txt", "hello");
        File.SetLastWriteTimeUtc(src, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Write("src/dir/b.txt", "bye");
        Directory.CreateDirectory(P("dst"));
        var reports = new List<OperationProgress>();
        await FileOperations.TransferAsync(new[] { src, P("src", "dir") }, P("dst"), false, _ => ConflictChoice.Skip, new Progress<OperationProgress>(reports.Add), default);
        Assert.Equal("hello", File.ReadAllText(P("dst", "a.txt")));
        Assert.Equal("bye", File.ReadAllText(P("dst", "dir", "b.txt")));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(P("dst", "a.txt")));
        Assert.True(File.Exists(src));

        await FileOperations.TransferAsync(new[] { P("src", "dir"), src }, P("moved"), true, _ => ConflictChoice.Skip, null, default);
        Assert.False(File.Exists(src)); Assert.False(Directory.Exists(P("src", "dir")));
        Assert.Equal("bye", File.ReadAllText(P("moved", "dir", "b.txt")));
        Assert.Equal("hello", File.ReadAllText(P("moved", "a.txt")));
    }

    [Fact]
    public async Task CopyIntoSameFolderNames()
    {
        var f = Write("a.txt", "1"); Directory.CreateDirectory(P("d")); Write("d/x.txt");
        await FileOperations.TransferAsync(new[] { f, P("d") }, Root, false, _ => throw new Exception("no conflict expected"), null, default);
        await FileOperations.TransferAsync(new[] { f }, Root, false, _ => throw new Exception(), null, default);
        Assert.True(File.Exists(P("a - Copy.txt"))); Assert.True(File.Exists(P("a - Copy (2).txt")));
        Assert.True(File.Exists(P("d - Copy", "x.txt")));
        await FileOperations.TransferAsync(new[] { f }, Root, true, _ => throw new Exception(), null, default); // move onto itself: no-op
        Assert.True(File.Exists(f));
    }

    [Fact]
    public async Task ConflictChoices()
    {
        Write("s/a.txt", "new"); Write("t/a.txt", "old");
        var src = P("s", "a.txt");
        await FileOperations.TransferAsync(new[] { src }, P("t"), false, _ => ConflictChoice.Skip, null, default);
        Assert.Equal("old", File.ReadAllText(P("t", "a.txt")));
        await FileOperations.TransferAsync(new[] { src }, P("t"), false, _ => ConflictChoice.KeepBoth, null, default);
        Assert.Equal("new", File.ReadAllText(P("t", "a (2).txt")));
        Assert.Equal("old", File.ReadAllText(P("t", "a.txt")));
        await FileOperations.TransferAsync(new[] { src }, P("t"), false, _ => ConflictChoice.Replace, null, default);
        Assert.Equal("new", File.ReadAllText(P("t", "a.txt")));
        Assert.Empty(Directory.GetFiles(P("t"), "*.frost-partial"));
    }

    [Fact]
    public async Task FolderIntoItselfThrows()
    {
        Write("d/x.txt"); Directory.CreateDirectory(P("d", "sub"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileOperations.TransferAsync(new[] { P("d") }, P("d"), false, _ => ConflictChoice.Skip, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileOperations.TransferAsync(new[] { P("d") }, P("d", "sub"), true, _ => ConflictChoice.Skip, null, default));
    }

    private sealed class InlineProgress(Action<OperationProgress> a) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress v) => a(v);
    }

    [Fact]
    public async Task CancelLeavesNoPartialFile()
    {
        var big = P("big.bin");
        File.WriteAllBytes(big, new byte[24 * 1024 * 1024]);
        Directory.CreateDirectory(P("out"));
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.BytesDone > 0) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileOperations.TransferAsync(new[] { big }, P("out"), false, _ => ConflictChoice.Replace, progress, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(P("out")));
        Assert.True(File.Exists(big));
    }

    [Fact]
    public async Task DeleteAndMeasure()
    {
        Write("m/a.txt", "123"); Write("m/s/b.txt", "4567"); Write("solo.txt", "89");
        var (bytes, files, folders) = await FileOperations.MeasureAsync(new[] { P("m"), P("solo.txt") }, default);
        Assert.Equal((9L, 3, 2), (bytes, files, folders));
        var ro = P("m", "s", "b.txt");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        FileOperations.DeletePermanently(new[] { P("m"), P("solo.txt"), P("missing.txt") });
        Assert.False(Directory.Exists(P("m"))); Assert.False(File.Exists(P("solo.txt")));
    }

    [Fact]
    public async Task SearchRun()
    {
        Write("a/one.png"); Write("a/b/two.PNG"); Write("a/b/three.txt"); Write("four.png.bak");
        var found = new List<string>();
        await foreach (var e in Search.RunAsync(Root, "*.png", true, default)) found.Add(e.Name);
        Assert.Equal(new[] { "one.png", "two.PNG" }, found.Order());

        found.Clear();
        await foreach (var e in Search.RunAsync(Root, "THREE", true, default)) found.Add(e.Name);
        Assert.Equal(new[] { "three.txt" }, found);

        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in Search.RunAsync(Root, "", true, cts.Token)) { } });
    }
}
