using System.IO.Compression;
using Frost.Core;
using Xunit;

namespace Frost.Core.Tests;

public class ArchivesTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("frost-zip").FullName;
    public void Dispose() => Directory.Delete(_d, true);

    private string Make(string rel, string? content)
    {
        string p = Path.Combine(_d, rel);
        if (content == null) Directory.CreateDirectory(p);
        else { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content); }
        return p;
    }

    [Fact]
    public async Task RoundTrip_NestedEmptyAndUnicode()
    {
        Make("Photos/a.txt", "hello");
        Make("Photos/sub/żółć ☃.txt", "unicode");
        Make("Photos/empty/inner", null);
        Make("single.txt", "x");
        var zip = Path.Combine(_d, "out.zip");
        await Archives.CompressAsync(new[] { Path.Combine(_d, "Photos"), Path.Combine(_d, "single.txt") }, zip, null, default);

        var dest = Path.Combine(_d, "ex");
        await Archives.ExtractAsync(zip, dest, null, default);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "Photos/a.txt")));
        Assert.Equal("unicode", File.ReadAllText(Path.Combine(dest, "Photos/sub/żółć ☃.txt")));
        Assert.True(Directory.Exists(Path.Combine(dest, "Photos/empty/inner")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(dest, "single.txt")));
        var (count, bytes, names) = Archives.Describe(zip, 200);
        Assert.Equal(names.Count, count);
        Assert.Equal(5 + 7 + 1, bytes);
    }

    [Fact]
    public async Task ZipSlip_Rejected_NothingWritten()
    {
        var zip = Path.Combine(_d, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("ok.txt").Open())) w.Write("ok");
            using (var w = new StreamWriter(z.CreateEntry("../escaped.txt").Open())) w.Write("bad");
        }
        var dest = Path.Combine(_d, "sub", "ex");
        Directory.CreateDirectory(Path.Combine(_d, "sub"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Archives.ExtractAsync(zip, dest, null, default));
        Assert.False(File.Exists(Path.Combine(_d, "sub", "escaped.txt")));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public async Task Extract_RefusesExistingFolder()
    {
        Make("a.txt", "a");
        var zip = Path.Combine(_d, "a.zip");
        await Archives.CompressAsync(new[] { Path.Combine(_d, "a.txt") }, zip, null, default);
        Directory.CreateDirectory(Path.Combine(_d, "ex"));
        await Assert.ThrowsAsync<IOException>(() => Archives.ExtractAsync(zip, Path.Combine(_d, "ex"), null, default));
    }

    [Fact]
    public void UniqueNaming()
    {
        Make("Photos/a.txt", "a");
        var p = Path.Combine(_d, "Photos");
        Assert.Equal(Path.Combine(_d, "Photos.zip"), Archives.ZipPathFor(new[] { p }, _d));
        File.WriteAllText(Path.Combine(_d, "Photos.zip"), "");
        Assert.Equal(Path.Combine(_d, "Photos (2).zip"), Archives.ZipPathFor(new[] { p }, _d));
        Assert.Equal(Path.Combine(_d, "Photos (2)"), Archives.ExtractFolderFor(Path.Combine(_d, "Photos.zip"), _d));
        Assert.Equal(Path.Combine(p, "Photos.zip"), Archives.ZipPathFor(new[] { Path.Combine(p, "a.txt"), Path.Combine(p, "b") }, p));
    }

    [Fact]
    public async Task Cancel_RemovesPartialOutput()
    {
        Make("big/a.bin", new string('x', 100000));
        var cts = new CancellationTokenSource(); cts.Cancel();
        var zip = Path.Combine(_d, "c.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Archives.CompressAsync(new[] { Path.Combine(_d, "big") }, zip, null, cts.Token));
        Assert.False(File.Exists(zip));

        await Archives.CompressAsync(new[] { Path.Combine(_d, "big") }, zip, null, default);
        var dest = Path.Combine(_d, "ex");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Archives.ExtractAsync(zip, dest, null, cts.Token));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public async Task Compress_ExistingZip_Survives()
    {
        Make("a.txt", "a");
        var zip = Path.Combine(_d, "keep.zip");
        File.WriteAllText(zip, "precious");
        await Assert.ThrowsAsync<IOException>(() => Archives.CompressAsync(new[] { Path.Combine(_d, "a.txt") }, zip, null, default));
        Assert.Equal("precious", File.ReadAllText(zip));
    }

    [Fact]
    public async Task Extract_ExistingFolder_Survives()
    {
        Make("a.txt", "a");
        var zip = Path.Combine(_d, "a.zip");
        await Archives.CompressAsync(new[] { Path.Combine(_d, "a.txt") }, zip, null, default);
        Make("ex/mine.txt", "mine");
        await Assert.ThrowsAsync<IOException>(() => Archives.ExtractAsync(zip, Path.Combine(_d, "ex"), null, default));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_d, "ex/mine.txt")));
    }

    [Fact]
    public async Task Compress_SkipsDirSymlink_KeepsFileSymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        Make("src/real/x.txt", "x"); Make("other/o.txt", "o");
        File.CreateSymbolicLink(Path.Combine(_d, "src/link.txt"), Path.Combine(_d, "other/o.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_d, "src/dirlink"), Path.Combine(_d, "other"));
        var zip = Path.Combine(_d, "s.zip");
        int skipped = await Archives.CompressAsync(new[] { Path.Combine(_d, "src") }, zip, null, default);
        Assert.Equal(1, skipped);
        var names = Archives.Describe(zip, 50).Names;
        Assert.Contains("src/link.txt", names);
        Assert.DoesNotContain(names, n => n.StartsWith("src/dirlink"));
    }

    [Fact]
    public async Task Extract_MovesIntoPlace_NoTempLeft_ReturnsFinal()
    {
        Make("a.txt", "a");
        var zip = Path.Combine(_d, "a.zip");
        await Archives.CompressAsync(new[] { Path.Combine(_d, "a.txt") }, zip, null, default);
        var dest = Path.Combine(_d, "ex");
        Assert.Equal(dest, await Archives.ExtractAsync(zip, dest, null, default));
        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.DoesNotContain(Directory.GetDirectories(_d), d => Path.GetFileName(d).StartsWith(".frost-extract-"));
    }

    [Fact]
    public async Task Compress_DanglingSymlink_Skipped()
    {
        if (OperatingSystem.IsWindows()) return;
        Make("src/ok.txt", "ok");
        File.CreateSymbolicLink(Path.Combine(_d, "src/dead.txt"), Path.Combine(_d, "nowhere"));
        var zip = Path.Combine(_d, "s.zip");
        int skipped = await Archives.CompressAsync(new[] { Path.Combine(_d, "src") }, zip, null, default);
        Assert.Equal(1, skipped);
        Assert.Equal(new[] { "src/", "src/ok.txt" }, Archives.Describe(zip, 50).Names.Order().ToArray());
    }
}
