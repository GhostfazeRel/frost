using Frost.Core;

namespace Frost.Core.Tests;

public sealed class BatchRenameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("frost-br-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private string[] Make(params string[] names)
    {
        foreach (var n in names) File.WriteAllText(Path.Combine(_dir, n), n);
        return names.Select(n => Path.Combine(_dir, n)).ToArray();
    }
    private static string[] Names(IReadOnlyList<BatchRenameItem> p) => p.Select(i => i.NewName).ToArray();
    private static string? Err(IReadOnlyList<BatchRenameItem> p, int i = 0) => p[i].Error;

    [Fact] public void PlainReplaceKeepsExtensionAndIgnoresCaseByDefault()
    {
        var p = BatchRename.Plan(Make("Foo.txt", "xfoo.txt"), new() { Find = "foo", Replace = "bar" });
        Assert.Equal(new[] { "bar.txt", "xbar.txt" }, Names(p));
    }
    [Fact] public void MatchCaseAndLiteralDollar()
    {
        var p = BatchRename.Plan(Make("Foo.txt", "foo.txt"), new() { Find = "foo", Replace = "$1", MatchCase = true });
        Assert.Equal(new[] { "Foo.txt", "$1.txt" }, Names(p));
    }
    [Fact] public void RegexGroups()
    {
        var p = BatchRename.Plan(Make("a-1.txt"), new() { Find = @"(\w)-(\d)", Replace = "$2_$1", UseRegex = true });
        Assert.Equal("1_a.txt", p[0].NewName);
    }
    [Fact] public void InvalidRegexIsAnError()
    {
        var p = BatchRename.Plan(Make("a.txt"), new() { Find = "(", UseRegex = true });
        Assert.NotNull(Err(p));
    }
    [Fact] public void PrefixSuffixGoAroundNameNotExtension()
    {
        Assert.Equal("pre_a_suf.txt", BatchRename.Plan(Make("a.txt"), new() { Prefix = "pre_", Suffix = "_suf" })[0].NewName);
    }
    [Fact] public void IncludeExtensionAppliesToWholeName()
    {
        var p = BatchRename.Plan(Make("a.txt"), new() { Suffix = "_x", IncludeExtension = true });
        Assert.Equal("a.txt_x", p[0].NewName);
        p = BatchRename.Plan(Make("b.TXT"), new() { Case = BatchCase.Lower, IncludeExtension = true });
        Assert.Equal("b.txt", p[0].NewName);
    }
    [Fact] public void NumberingStartStepWidth()
    {
        var p = BatchRename.Plan(Make("a.txt", "b.txt", "c.txt"), new() { Prefix = "img{n}_", Start = 8, Step = 2, Width = 3 });
        Assert.Equal(new[] { "img008_a.txt", "img010_b.txt", "img012_c.txt" }, Names(p));
    }
    [Fact] public void NumberTokenInReplacement()
    {
        var p = BatchRename.Plan(Make("a.txt", "b.txt"), new() { Find = "^.*$", UseRegex = true, Replace = "n{n}" });
        Assert.Equal(new[] { "n1.txt", "n2.txt" }, Names(p));
    }
    [Fact] public void CaseModes()
    {
        var f = Make("hello WORLD.txt");
        Assert.Equal("hello world.txt", BatchRename.Plan(f, new() { Case = BatchCase.Lower })[0].NewName);
        Assert.Equal("HELLO WORLD.txt", BatchRename.Plan(f, new() { Case = BatchCase.Upper })[0].NewName);
        Assert.Equal("Hello World.txt", BatchRename.Plan(f, new() { Case = BatchCase.Title })[0].NewName);
    }
    [Fact] public void FolderIsRenamedWholeEvenWithDot()
    {
        var d = Path.Combine(_dir, "v1.2"); Directory.CreateDirectory(d);
        Assert.Equal("v1.2_x", BatchRename.Plan(new[] { d }, new() { Suffix = "_x" })[0].NewName);
    }

    [Fact] public void ErrorInvalidCharacters() => Assert.Contains("aren't allowed", Err(BatchRename.Plan(Make("a.txt"), new() { Prefix = "a:" }))!);
    [Fact] public void ErrorReservedName() => Assert.NotNull(Err(BatchRename.Plan(Make("a.txt"), new() { Find = "a", Replace = "CON" })));
    [Fact] public void ErrorTrailingDot() => Assert.NotNull(Err(BatchRename.Plan(Make("a.txt"), new() { Find = ".txt", Replace = ".", IncludeExtension = true })));
    [Fact] public void ErrorEmptyName() => Assert.Contains("empty", Err(BatchRename.Plan(Make("a"), new() { Find = "a", Replace = "" }))!);
    [Fact] public void ErrorDuplicateInBatch()
    {
        var p = BatchRename.Plan(Make("a1.txt", "a2.txt"), new() { Find = @"\d", UseRegex = true });
        Assert.NotNull(Err(p, 0)); Assert.NotNull(Err(p, 1));
    }
    [Fact] public void ErrorDuplicateIgnoresCase()
    {
        var p = BatchRename.Plan(Make("a1.txt", "A2.txt"), new() { Find = @"\d", UseRegex = true });
        Assert.NotNull(Err(p, 0));
    }
    [Fact] public void ErrorTargetExists()
    {
        var f = Make("a.txt", "b.txt");
        var p = BatchRename.Plan(new[] { f[0] }, new() { Find = "a", Replace = "b" });
        Assert.Contains("already exists", Err(p)!);
    }
    [Fact] public void ErrorBatchItemCollidesWithUnchangedItem()
    {
        var p = BatchRename.Plan(Make("a.txt", "b.txt"), new() { Find = "^a$", UseRegex = true, Replace = "b" });
        Assert.NotNull(Err(p, 0)); // b.txt stays b.txt
    }
    [Fact] public void UnchangedItemsAreFineAndSkipped()
    {
        var p = BatchRename.Plan(Make("a.txt"), new());
        Assert.Null(Err(p));
        Assert.Empty(BatchRename.Apply(p));
    }

    [Fact] public void ApplyRenamesAndReturnsPairs()
    {
        var f = Make("a.txt", "b.txt");
        var applied = BatchRename.Apply(BatchRename.Plan(f, new() { Prefix = "x_" }));
        Assert.Equal(new[] { (f[0], Path.Combine(_dir, "x_a.txt")), (f[1], Path.Combine(_dir, "x_b.txt")) }, applied);
        Assert.True(File.Exists(Path.Combine(_dir, "x_b.txt")));
        Assert.False(File.Exists(f[0]));
    }
    [Fact] public void ApplySwap()
    {
        var f = Make("a.txt", "b.txt");
        var plan = new[] { new BatchRenameItem(f[0], "b.txt", null), new BatchRenameItem(f[1], "a.txt", null) };
        BatchRename.Apply(plan);
        Assert.Equal("b.txt", File.ReadAllText(f[0]));
        Assert.Equal("a.txt", File.ReadAllText(f[1]));
    }
    [Fact] public void PlanAllowsSwapAndCaseOnly()
    {
        var f = Make("a.txt");
        Assert.Null(Err(BatchRename.Plan(f, new() { Case = BatchCase.Upper })));
    }
    [Fact] public void ApplyCaseOnlyRename()
    {
        var f = Make("a.txt");
        BatchRename.Apply(BatchRename.Plan(f, new() { Case = BatchCase.Upper }));
        Assert.Contains("A.txt", Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
    [Fact] public void ApplyRefusesPlanWithErrors()
    {
        var f = Make("a.txt");
        Assert.Throws<InvalidOperationException>(() => BatchRename.Apply(new[] { new BatchRenameItem(f[0], "b.txt", "bad") }));
        Assert.True(File.Exists(f[0]));
    }
    [Fact] public void ApplyRollsBackOnFailure()
    {
        var f = Make("a.txt", "b.txt");
        var plan = BatchRename.Plan(f, new() { Prefix = "x_" });
        File.WriteAllText(Path.Combine(_dir, "x_b.txt"), "intruder"); // appears after planning
        Assert.ThrowsAny<IOException>(() => BatchRename.Apply(plan));
        Assert.Equal("a.txt", File.ReadAllText(f[0]));
        Assert.Equal("b.txt", File.ReadAllText(f[1]));
        Assert.Equal("intruder", File.ReadAllText(Path.Combine(_dir, "x_b.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "x_a.txt")));
        Assert.Equal(3, Directory.GetFileSystemEntries(_dir).Length);
    }
}

public sealed class BatchRenameSafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("frost-br2-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private string P(string n) => Path.Combine(_dir, n);
    private string[] Make(params string[] names) { foreach (var n in names) File.WriteAllText(P(n), n); return names.Select(P).ToArray(); }

    [Fact] public void RollbackFailureIsReportedWithStrandedNames()
    {
        var f = Make("a.txt", "b.txt");
        var plan = BatchRename.Plan(f, new() { Prefix = "x_" });
        int steps = 0;
        // After phase 1 (2 steps) and the first phase-2 rename, occupy a.txt and fail: rollback of a can't restore its name.
        var ex = Assert.Throws<BatchRenameRollbackException>(() => BatchRename.Apply(plan, () =>
        {
            if (++steps == 3) { File.WriteAllText(P("a.txt"), "intruder"); throw new IOException("boom"); }
        }));
        Assert.Contains(".frost-rename-", ex.Message);
        Assert.Contains("a.txt", ex.Message);
        Assert.DoesNotContain("Nothing was renamed", ex.Message);
        Assert.True(File.Exists(f[1])); // the other file was restored
        Assert.Equal("intruder", File.ReadAllText(f[0]));
    }
    [Fact] public void RollbackAfterMidPhaseTwoFailureRestoresAll()
    {
        var f = Make("a.txt", "b.txt", "c.txt");
        var plan = BatchRename.Plan(f, new() { Prefix = "x_" });
        File.WriteAllText(P("x_c.txt"), "intruder");
        Assert.ThrowsAny<IOException>(() => BatchRename.Apply(plan));
        Assert.All(new[] { "a.txt", "b.txt", "c.txt" }, n => Assert.Equal(n, File.ReadAllText(P(n))));
        Assert.Equal(4, Directory.GetFileSystemEntries(_dir).Length);
    }
    [Fact] public void SlowRegexFailsWholeBatchQuickly()
    {
        var f = Make(Enumerable.Range(0, 50).Select(i => new string('a', 40) + "!" + i + ".txt").ToArray());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = BatchRename.Plan(f, new() { Find = "(a+)+$", UseRegex = true, Replace = "x" });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.All(p, i => Assert.Contains("too slow", i.Error));
    }
    [Fact] public void NumberFollowsGivenOrderAndLiteralBraceNInNamesIsKept()
    {
        var f = Make("{n}a.txt", "b.txt");
        var p = BatchRename.Plan(new[] { f[1], f[0] }, new() { Prefix = "{n}-" });
        Assert.Equal(new[] { "1-b.txt", "2-{n}a.txt" }, p.Select(i => i.NewName));
    }
    [Fact] public void CancelledPlanThrows()
    {
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BatchRename.Plan(Make("a.txt"), new(), cts.Token));
    }
}
