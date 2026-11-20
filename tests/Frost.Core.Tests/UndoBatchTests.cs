using Frost.Core;

namespace Frost.Core.Tests;

public sealed class UndoBatchTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("frost-undobatch").FullName;
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }
    private string P(string n) => Path.Combine(_d, n);
    private static bool Del(IReadOnlyList<string> ps) { foreach (var p in ps) File.Delete(p); return true; }
    private static Task<UndoOutcome> Undo(UndoHistory h) => h.UndoAsync(Del, _ => false, null, default);
    private static Task<UndoOutcome> Redo(UndoHistory h) => h.RedoAsync(Del, _ => false, null, default);

    [Fact]
    public async Task Swap_is_one_entry_and_undo_redo_restore_it()
    {
        File.WriteAllText(P("a.txt"), "A");
        File.WriteAllText(P("b.txt"), "BB");
        var applied = BatchRename.Apply(new[] { new BatchRenameItem(P("a.txt"), "b.txt", null), new BatchRenameItem(P("b.txt"), "a.txt", null) });
        var h = new UndoHistory();
        h.RecordBatchRename(applied);
        await h.FingerprintsReady;
        Assert.Equal(1, h.UndoCount);
        Assert.Equal("BB", File.ReadAllText(P("a.txt")));

        Assert.True((await Undo(h)).Ok);
        Assert.Equal("A", File.ReadAllText(P("a.txt")));
        Assert.Equal("BB", File.ReadAllText(P("b.txt")));
        Assert.True((await Redo(h)).Ok);
        Assert.Equal("BB", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public async Task Undo_refuses_when_a_name_is_taken_and_touches_nothing()
    {
        File.WriteAllText(P("a.txt"), "A");
        File.WriteAllText(P("b.txt"), "B");
        var applied = BatchRename.Apply(new[] { new BatchRenameItem(P("a.txt"), "x.txt", null), new BatchRenameItem(P("b.txt"), "y.txt", null) });
        var h = new UndoHistory();
        h.RecordBatchRename(applied);
        await h.FingerprintsReady;
        File.WriteAllText(P("b.txt"), "other");
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.True(File.Exists(P("x.txt")) && File.Exists(P("y.txt")) && h.CanUndo);
    }
}
