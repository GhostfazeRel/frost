using Frost.Core;

namespace Frost.Core.Tests;

public class PaletteTests
{
    [Fact]
    public void NoMatchIsNull() => Assert.Null(Fuzzy.Score("xyz", "New tab"));

    [Fact]
    public void QueryLongerThanCandidateIsNull() => Assert.Null(Fuzzy.Score("abcd", "abc"));

    [Fact]
    public void IndicesAreMatchedCharacters()
    {
        var m = Fuzzy.Score("nt", "New tab")!;
        Assert.Equal(new[] { 0, 4 }, m.Indices);
    }

    [Fact]
    public void CaseInsensitive() => Assert.NotNull(Fuzzy.Score("NEW", "new tab"));

    [Fact]
    public void PrefixBeatsLateMatch()
    {
        var r = Fuzzy.Rank(new[] { "Reopen closed tab", "Open terminal here", "Toggle open" }, "open", s => s);
        Assert.Equal("Open terminal here", r[0].Item);
    }

    [Fact]
    public void ConsecutiveBeatsScattered()
    {
        var a = Fuzzy.Score("doc", "Documents")!.Score;
        var b = Fuzzy.Score("doc", "Data of cats")!.Score;
        Assert.True(a > b);
    }

    [Fact]
    public void WordStartsBeatMidWord()
    {
        var a = Fuzzy.Score("ns", @"C:\New\Stuff")!;
        var b = Fuzzy.Score("ns", @"C:\Banners")!;
        Assert.True(a.Score > b.Score);
        Assert.Equal(new[] { 3, 7 }, a.Indices);
    }

    [Fact]
    public void EmptyQueryKeepsOrder()
    {
        var r = Fuzzy.Rank(new[] { "b", "a", "c" }, "", s => s);
        Assert.Equal(new[] { "b", "a", "c" }, r.Select(x => x.Item));
    }

    [Fact]
    public void RankFiltersAndSorts()
    {
        var r = Fuzzy.Rank(new[] { "Sort by size", "Show hidden", "Refresh" }, "sh", s => s);
        Assert.Equal(2, r.Count);
        Assert.Equal("Show hidden", r[0].Item);
    }

    [Fact]
    public void RecentDedupesMovesToFrontAndCaps()
    {
        var l = new List<string>();
        RecentList.Add(l, @"C:\A"); RecentList.Add(l, @"C:\B"); RecentList.Add(l, @"c:\a\");
        Assert.Equal(new[] { @"c:\a\", @"C:\B" }, l);
        for (int i = 0; i < 60; i++) RecentList.Add(l, @"D:\" + i);
        Assert.Equal(RecentList.Max, l.Count);
        Assert.Equal(@"D:\59", l[0]);
    }
}
