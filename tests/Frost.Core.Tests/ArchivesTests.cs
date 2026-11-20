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
}
