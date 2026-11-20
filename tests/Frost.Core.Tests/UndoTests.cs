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
    private static Task<UndoOutcome> Undo(UndoHistory h, Func<string, bool?>? ask = null) => h.UndoAsync(Del, ask ?? (_ => false), null, default);
    private static Task<UndoOutcome> Redo(UndoHistory h) => h.RedoAsync(Del, _ => false, null, default);

    [Fact]
    public async Task Non_redoable_create_is_undo_only()
    {
        var h = new UndoHistory();
        string f = Make("out.zip");
        h.RecordCreate(f, false, redoable: false);
        await h.FingerprintsReady;
        Assert.True((await Undo(h)).Ok);
        Assert.False(File.Exists(f));
        Assert.False(h.CanRedo);
    }

    [Fact]
    public async Task Rename_undo_and_redo()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        await h.FingerprintsReady;
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
        await h.FingerprintsReady;
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
        await h.FingerprintsReady;
        File.Delete(b);
        Assert.False((await Undo(h)).Ok);
    }

    [Fact]
    public async Task Create_undo_recycles_only_if_it_exists_and_redo_recreates()
    {
        var h = new UndoHistory();
        string f = FileOperations.CreateFolder(_d);
        h.RecordCreate(f, true);
        await h.FingerprintsReady;
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
        await h.FingerprintsReady;
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
        await h.FingerprintsReady;
        Assert.True((await Undo(h)).Ok);
        Assert.True(File.Exists(src));
        Assert.True((await Redo(h)).Ok);
        Assert.True(File.Exists(Path.Combine(sub, "m.txt")) && !File.Exists(src));

        Make("m.txt", "blocker");
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.Equal("blocker", File.ReadAllText(src));
        Assert.True(File.Exists(Path.Combine(sub, "m.txt")));
        Assert.True(h.CanUndo); // refused entry stays so it can be retried
        File.Delete(src);
        Assert.True((await Undo(h)).Ok);
        Assert.True(File.Exists(src));
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
        await h.FingerprintsReady;
        await Undo(h);
        Assert.True(h.CanRedo);
        h.RecordCreate(P("zzz"), false);
        await h.FingerprintsReady;
        Assert.False(h.CanRedo);
    }

    [Fact]
    public async Task Modified_copy_is_not_recycled_without_confirmation()
    {
        var h = new UndoHistory();
        string src = Make("f.txt", "data");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { src }, _d, false, _ => ConflictChoice.Skip, null, default, done);
        h.RecordTransfer(false, done);
        await h.FingerprintsReady;
        string copy = done[0].Destination;
        File.WriteAllText(copy, "my edits, longer");
        string? asked = null;
        var r = await Undo(h, m => { asked = m; return false; });
        Assert.False(r.Ok);
        Assert.Contains("changed since it was copied", asked);
        Assert.Equal("my edits, longer", File.ReadAllText(copy));
        Assert.False(h.CanUndo); // "Leave it": the user decided, entry dropped so older ones stay reachable
    }

    [Fact]
    public async Task Modified_copy_confirmed_is_recycled_and_cancel_keeps_the_entry()
    {
        var h = new UndoHistory();
        string src = Make("f.txt", "data");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { src }, _d, false, _ => ConflictChoice.Skip, null, default, done);
        h.RecordTransfer(false, done);
        await h.FingerprintsReady;
        string copy = done[0].Destination;
        File.WriteAllText(copy, "my edits, longer");
        Assert.False((await Undo(h, _ => null)).Ok); // Cancel aborts the whole undo
        Assert.True(h.CanUndo && File.Exists(copy));
        Assert.True((await Undo(h, _ => true)).Ok);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task Copy_deleted_outside_frost_drops_the_entry_so_older_undo_works()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        string src = Make("f.txt", "data");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { src }, _d, false, _ => ConflictChoice.Skip, null, default, done);
        h.RecordTransfer(false, done);
        await h.FingerprintsReady;
        File.Delete(done[0].Destination);
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.Contains("outside Frost", r.Message);
        Assert.Contains("removed from the history", r.Message);
        Assert.True((await Undo(h)).Ok); // the older rename is reachable again
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task Filled_created_folder_needs_confirmation_but_empty_one_does_not()
    {
        var h = new UndoHistory();
        string f = FileOperations.CreateFolder(_d);
        h.RecordCreate(f, true);
        await h.FingerprintsReady;
        File.WriteAllText(Path.Combine(f, "work.txt"), "x");
        int asks = 0;
        Assert.False((await Undo(h, _ => { asks++; return false; })).Ok);
        Assert.Equal(1, asks);
        Assert.True(File.Exists(Path.Combine(f, "work.txt")));
        Assert.False(h.CanUndo);
        string g = FileOperations.CreateFolder(_d);
        h.RecordCreate(g, true);
        await h.FingerprintsReady;
        Assert.True((await Undo(h, _ => { asks++; return false; })).Ok); // still empty: no question
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task Huge_folder_is_unverifiable_so_it_asks()
    {
        string big = Directory.CreateDirectory(P("big")).FullName;
        for (int i = 0; i < ItemPrint.MaxEntries + 1; i++) File.WriteAllBytes(Path.Combine(big, $"{i}"), Array.Empty<byte>());
        Assert.Null(ItemPrint.Of(big));
        var h = new UndoHistory();
        h.RecordCreate(big, true);
        await h.FingerprintsReady;
        bool asked = false;
        await Undo(h, _ => { asked = true; return false; });
        Assert.True(asked);
    }

    [Fact]
    public async Task Rename_undo_asks_when_a_different_file_now_has_the_name()
    {
        var h = new UndoHistory();
        string a = Make("a.txt", "one"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        await h.FingerprintsReady;
        File.Delete(b);
        Make("b.txt", "something else entirely");
        bool asked = false;
        var r = await Undo(h, _ => { asked = true; return false; });
        Assert.True(asked);
        Assert.False(r.Ok);
        Assert.True(File.Exists(b) && !File.Exists(a));
        Assert.False(h.CanUndo); // declined: dropped
    }

    [Fact]
    public async Task Created_item_deleted_outside_frost_is_dropped()
    {
        var h = new UndoHistory();
        string f = FileOperations.CreateFolder(_d);
        h.RecordCreate(f, true);
        await h.FingerprintsReady;
        Directory.Delete(f);
        var r = await Undo(h);
        Assert.False(r.Ok);
        Assert.False(h.CanUndo);
    }

    [Fact]
    public async Task Pending_or_missing_fingerprint_is_treated_as_unverifiable()
    {
        var h = new UndoHistory();
        string f = FileOperations.CreateFolder(_d);
        h.RecordCreate(f, true); // no await FingerprintsReady
        bool asked = false;
        var task = Undo(h, _ => { asked = true; return false; });
        await task;
        // either the fingerprint finished first (no question, recycled) or it was pending (question): never silently wrong
        Assert.True(asked ? Directory.Exists(f) : !Directory.Exists(f));
    }

    [Fact]
    public async Task Fingerprint_budget_is_shared_across_the_operation()
    {
        string a = Directory.CreateDirectory(P("a")).FullName, b = Directory.CreateDirectory(P("b")).FullName;
        for (int i = 0; i < 3000; i++) { File.WriteAllBytes(Path.Combine(a, $"{i}"), new byte[0]); File.WriteAllBytes(Path.Combine(b, $"{i}"), new byte[0]); }
        int budget = ItemPrint.MaxEntries;
        Assert.NotNull(ItemPrint.Of(a, ref budget));
        Assert.Null(ItemPrint.Of(b, ref budget)); // 3000 + 3000 > 5000
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Refused_undo_keeps_entry_and_succeeds_after_fixing_state()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        await h.FingerprintsReady;
        Make("a.txt", "blocker");
        Assert.False((await Undo(h)).Ok);
        File.Delete(a);
        Assert.True((await Undo(h)).Ok);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task Partial_undo_reports_and_keeps_the_remainder()
    {
        var h = new UndoHistory();
        string s1 = Make("p1.txt"), s2 = Make("p2.txt");
        var done = new List<TransferredItem>();
        await FileOperations.TransferAsync(new[] { s1, s2 }, Directory.CreateDirectory(P("d")).FullName, false, _ => ConflictChoice.Skip, null, default, done);
        h.RecordTransfer(false, done);
        await h.FingerprintsReady;
        // recycle only deletes the first path (second "fails")
        bool Half(IReadOnlyList<string> ps) { File.Delete(ps[0]); return false; }
        var r = await h.UndoAsync(Half, _ => true, null, default);
        Assert.False(r.Ok);
        Assert.Contains("p2.txt", r.Message);
        Assert.Equal("copy of \"p2.txt\"", h.UndoLabel); // remainder is the undo entry
        Assert.Equal("copy of \"p1.txt\"", h.RedoLabel);
        Assert.True((await Undo(h)).Ok);
        Assert.False(h.CanUndo);
    }

    [Fact]
    public async Task TransferExact_reports_skipped_items()
    {
        string src = Make("s.txt"), dst = Make("t.txt", "existing");
        var done = new List<TransferredItem>();
        var skipped = await FileOperations.TransferExactAsync(new[] { new TransferredItem(src, dst) }, false, null, default, done);
        Assert.Single(skipped);
        Assert.Empty(done);
        Assert.Equal("existing", File.ReadAllText(dst));
    }

    [Fact]
    public async Task Stale_redo_is_not_created_when_an_operation_is_recorded_during_undo()
    {
        var h = new UndoHistory();
        string a = Make("a.txt"), b = FileOperations.Rename(a, "b.txt");
        h.RecordRename(a, b);
        await h.FingerprintsReady;
        File.WriteAllText(b, "changed so that confirm is asked");
        var r = await h.UndoAsync(Del, _ => { h.RecordCreate(P("n"), true); return true; }, null, default);
        Assert.True(r.Ok);
        Assert.False(h.CanRedo);
    }
}
