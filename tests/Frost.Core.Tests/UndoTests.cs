using Frost.Core;

namespace Frost.Core.Tests;

public sealed class UndoTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("frost-undo").FullName;
    public void Dispose() { try { Directory.Delete(_d, true); } catch { } }

    private string P(string n) => Path.Combine(_d, n);
    private string Make(string n, string text = "x") { File.WriteAllText(P(n), text); return P(n); }
    // Fake recycle bin: just deletes.
    private static bool Del(IReadOnlyList<string> ps) { foreach (var p in ps) { if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p); } return true; }
    private static Task<UndoOutcome> Undo(UndoHistory h) => h.UndoAsync(Del, null, default);
    private static Task<UndoOutcome> Redo(UndoHistory h) => h.RedoAsync(Del, null, default);

    [Fact]
    public async Task Rename_undo_and_redo()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        Assert.Equal("rename of \"b.txt\"", h.UndoLabel);
        Assert.True((await Undo(h)).Ok);
        Assert.True(File.Exists(a) && !File.Exists(b));
        Assert.True(h.CanRedo && !h.CanUndo);
        Assert.True((await Redo(h)).Ok);
        Assert.True(File.Exists(b) && !File.Exists(a));
    }

    [Fact]
    public async Task Rename_undo_refuses_when_original_name_is_taken()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        Make("a.txt", "other");
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.Equal("other", File.ReadAllText(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task Rename_undo_refuses_when_item_is_gone()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        File.Delete(b);
        Assert.False((await Undo(h)).Ok);
    }

    [Fact]
    public async Task Create_undo_recycles_only_if_it_exists_and_redo_recreates()
    {
        var h = new UndoHistory();
        string f = FileOperations.CreateFolder(_d);
        h.RecordCreate(f, true);
        Assert.True((await Undo(h)).Ok);
        Assert.False(Directory.Exists(f));
        Assert.True((await Redo(h)).Ok);
        Assert.True(Directory.Exists(f));
        Directory.Delete(f);
        Assert.False((await Undo(h)).Ok); // already gone
    }

    [Fact]
    public async Task Copy_records_actual_names_and_undo_recycles_only_the_copies()
    {
        var h = new UndoHistory();
        string src = Make("f.txt", "data");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { src }, _d, false, _ => ConflictChoice.Skip, null, default, done);
        Assert.Single(done);
        Assert.Equal(P("f - Copy.txt"), done[0].Destination);
        h.RecordTransfer(false, done);
        Assert.True((await Undo(h)).Ok);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(P("f - Copy.txt")));
        Assert.True((await Redo(h)).Ok);
        Assert.Equal("data", File.ReadAllText(P("f - Copy.txt")));
    }

    [Fact]
    public async Task KeepBoth_collision_name_is_reported_but_replace_and_skip_are_not()
    {
        string sub = Directory.CreateDirectory(P("dst")).FullName;
        File.WriteAllText(Path.Combine(sub, "f.txt"), "old");
        string src = Make("f.txt", "new");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { src }, sub, false, _ => ConflictChoice.KeepBoth, null, default, done);
        Assert.Equal(Path.Combine(sub, "f (2).txt"), done.Single().Destination);

        done.Clear();
        await FileOperations.TransferAsync(new[] { src }, sub, false, _ => ConflictChoice.Replace, null, default, done);
        Assert.Empty(done); // overwrote user data: not undoable
        await FileOperations.TransferAsync(new[] { src }, sub, false, _ => ConflictChoice.Skip, null, default, done);
        Assert.Empty(done);
    }

    [Fact]
    public async Task Move_undo_moves_back_and_refuses_when_original_spot_is_taken()
    {
        var h = new UndoHistory();
        string sub = Directory.CreateDirectory(P("dst")).FullName;
        string src = Make("m.txt");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { src }, sub, true, _ => ConflictChoice.Skip, null, default, done);
        h.RecordTransfer(true, done);
        Assert.True((await Undo(h)).Ok);
        Assert.True(File.Exists(src));
        Assert.True((await Redo(h)).Ok);
        Assert.True(File.Exists(Path.Combine(sub, "m.txt")) && !File.Exists(src));

        Make("m.txt", "blocker");
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.Equal("blocker", File.ReadAllText(src));
        Assert.True(File.Exists(Path.Combine(sub, "m.txt")));
        Assert.False(h.CanUndo); // refused entry is dropped
    }

    [Fact]
    public async Task Recycle_undo_offers_recycle_bin()
    {
        var h = new UndoHistory();
        h.RecordRecycle(new[] { P("gone.txt") });
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.True(r.CanOpenRecycleBin);
    }

    [Fact]
    public void Cap_is_50_and_new_operation_clears_redo()
    {
        var h = new UndoHistory();
        for (int i = 0; i < 60; i++) h.RecordRename(P($"a{i}"), P($"b{i}"));
        Assert.Equal(50, h.UndoCount);
        Assert.Equal("rename of \"b59\"", h.UndoLabel);
    }

    [Fact]
    public async Task New_operation_clears_redo()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        await Undo(h);
        Assert.True(h.CanRedo);
        h.RecordCreate(P("zzz"), false);
        Assert.False(h.CanRedo);
    }
}
