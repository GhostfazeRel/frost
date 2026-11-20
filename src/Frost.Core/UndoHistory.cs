namespace Frost.Core;

public abstract record UndoOp;
// Print = fingerprints computed off the UI thread (see ItemPrint); a pending or failed one counts as unverifiable.
public sealed record RenameOp(string From, string To, Task<string?[]> Print) : UndoOp;
public sealed record CreateOp(string Path, bool IsDirectory, Task<string?[]> Print) : UndoOp;
public sealed record TransferOp(bool Move, IReadOnlyList<TransferredItem> Items, Task<string?[]>? Prints = null) : UndoOp;
public sealed record RecycleOp(IReadOnlyList<string> Paths) : UndoOp;

/// <param name="Message">What happened, or why it was refused.</param>
/// <param name="CanOpenRecycleBin">The App should offer an "Open Recycle Bin" button (deletes can't be undone by Frost).</param>
public sealed record UndoOutcome(bool Ok, string Message, bool CanOpenRecycleBin = false);

/// <summary>"Is this still the item we made?" fingerprints. Files: length + last write time. Folders: every entry's
/// relative path, length and mtime. Null when the item is missing, unreadable, or the shared entry budget (MaxEntries
/// folder entries per operation; files are free) ran out: unverifiable.</summary>
public static class ItemPrint
{
    public const int MaxEntries = 5000;

    public static string? Of(string path) { int budget = MaxEntries; return Of(path, ref budget); }

    public static string? Of(string path, ref int budget)
    {
        try
        {
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return $"F|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
            }
            if (!Directory.Exists(path)) return null;
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var entries = new List<string>();
            var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
            foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opt))
            {
                if (--budget < 0) return null;
                string rel = Path.GetRelativePath(root, e.FullName);
                entries.Add(e is FileInfo fi ? $"{rel}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : rel + "|d");
            }
            entries.Sort(StringComparer.Ordinal);
            return "D|" + string.Join("\n", entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}


/// <summary>Reversible file operations, recorded as data. Undo/Redo check that every path is still in the expected
/// state and refuse (never overwrite) otherwise; copies/created items that changed since are only recycled after
/// `confirm` says yes (true = yes, false = leave it, null = cancel the whole undo, entry kept).
/// Refusals the user can fix (name taken...) keep the entry; items that are gone, or that the user chose to leave, drop
/// it so older entries stay reachable. A partly applied undo keeps the remainder and reports what was and wasn't done.
/// Fingerprints are computed on the thread pool. Use from the UI thread only.</summary>
public sealed class UndoHistory
{
    public const int Capacity = 50;
    private readonly List<UndoOp> _undo = new(), _redo = new();
    private long _version; // bumped by every Push so a running undo/redo can tell a new operation happened meanwhile
    private Task _prints = Task.CompletedTask;

    /// <summary>Completes when all fingerprints recorded so far are computed (tests; the App doesn't wait).</summary>
    public Task FingerprintsReady => _prints;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Short phrase for menus, e.g. "rename of \"a.txt\"", or null when empty.</summary>
    public string? UndoLabel => _undo.Count > 0 ? Describe(_undo[^1]) : null;
    public string? RedoLabel => _redo.Count > 0 ? Describe(_redo[^1]) : null;
    public int UndoCount => _undo.Count;

    /// <summary>Call right AFTER renaming. The entry is recorded at once; the item is fingerprinted in the background.</summary>
    public void RecordRename(string from, string to) { if (from != to) Push(new RenameOp(from, to, PrintAsync(new[] { to }))); }
    /// <summary>Call right AFTER creating the (empty) item.</summary>
    public void RecordCreate(string path, bool isDirectory) => Push(new CreateOp(path, isDirectory, PrintAsync(new[] { path })));
    /// <summary>Call right AFTER the transfer finished (copies are fingerprinted in the background).</summary>
    public void RecordTransfer(bool move, IEnumerable<TransferredItem> items)
    {
        var list = items.ToArray();
        if (list.Length > 0) Push(new TransferOp(move, list, move ? null : PrintAsync(list.Select(i => i.Destination).ToArray())));
    }
    /// <summary>Paths that went to the Recycle Bin. Frost can't restore them itself (see UndoAsync).</summary>
    public void RecordRecycle(IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        if (list.Length > 0) Push(new RecycleOp(list));
    }

    private void Push(UndoOp op)
    {
        _undo.Add(op);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        _version++;
    }

    // One shared entry budget for the whole operation, so a huge copy can't make the pool walk millions of entries.
    private Task<string?[]> PrintAsync(IReadOnlyList<string> paths)
    {
        var t = Task.Run(() =>
        {
            var r = new string?[paths.Count];
            try { int budget = ItemPrint.MaxEntries; for (int i = 0; i < r.Length; i++) r[i] = ItemPrint.Of(paths[i], ref budget); }
            catch (Exception) { } // whatever is left stays null = unverifiable
            return r;
        });
        _prints = Task.WhenAll(_prints, t);
        return t;
    }

    // Re-fingerprints off the UI thread and compares; pending/failed/unverifiable recorded prints count as changed.
    private static Task<bool[]> ChangedAsync(IReadOnlyList<string> paths, Task<string?[]>? recorded)
    {
        var rec = recorded is { IsCompletedSuccessfully: true } ? recorded.Result : null;
        return Task.Run(() =>
        {
            var changed = new bool[paths.Count];
            int budget = ItemPrint.MaxEntries;
            for (int i = 0; i < changed.Length; i++)
            {
                string? now = null;
                try { now = ItemPrint.Of(paths[i], ref budget); } catch (Exception) { }
                changed[i] = now == null || rec == null || rec[i] == null || now != rec[i];
            }
            return changed;
        });
    }

    private static Task<string?[]> Slice(Task<string?[]>? p, IEnumerable<int> idx)
    {
        var src = p is { IsCompletedSuccessfully: true } ? p.Result : null;
        return Task.FromResult(idx.Select(i => src?[i]).ToArray());
    }

    /// <summary>Reverses the newest operation. `recycle` sends paths to the Recycle Bin and returns true on success;
    /// `confirm` asks a yes/no/cancel question (default should be No).</summary>
    public Task<UndoOutcome> UndoAsync(Func<IReadOnlyList<string>, bool> recycle, Func<string, bool?> confirm,
        IProgress<OperationProgress>? progress, CancellationToken ct) => Go(false, recycle, confirm, progress, ct);

    public Task<UndoOutcome> RedoAsync(Func<IReadOnlyList<string>, bool> recycle, Func<string, bool?> confirm,
        IProgress<OperationProgress>? progress, CancellationToken ct) => Go(true, recycle, confirm, progress, ct);

    // Problem null = success. Drop = remove the entry (can't be fixed by the user). Remainder = partly done, keep this.
    private sealed record Step(string? Problem = null, UndoOp? Remainder = null, UndoOp? Done = null, bool Drop = false, string? Note = null);

    private static Step Gone(string why) => new(why, Drop: true);

    private async Task<UndoOutcome> Go(bool forward, Func<IReadOnlyList<string>, bool> recycle, Func<string, bool?> confirm,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var from = forward ? _redo : _undo;
        var to = forward ? _undo : _redo;
        string verb = forward ? "redo" : "undo", past = forward ? "Redid" : "Undid";
        if (from.Count == 0) return new(false, $"Nothing to {verb}.");
        var op = from[^1];
        string what = Describe(op);
        // Restoring from the Recycle Bin can't be done reliably without the Windows shell, so offer to open it instead.
        if (op is RecycleOp)
        {
            from.RemoveAt(from.Count - 1);
            return new(false, $"Can't undo {what} — restore the items from the Recycle Bin.", CanOpenRecycleBin: true);
        }

        long version = _version;
        Step step;
        try { step = await Apply(op, forward, recycle, confirm, progress, ct); }
        catch (OperationCanceledException) { step = new Step("cancelled."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { step = new Step(e.Message); }

        int idx = IndexOfRef(from, op); // gone if a new operation cleared the redo stack meanwhile
        if (step.Drop)
        {
            if (idx >= 0) from.RemoveAt(idx);
            return new(false, $"Nothing to {verb} for the {what}: {step.Problem} It was removed from the history.");
        }
        if (step.Problem == null)
        {
            if (idx >= 0) from.RemoveAt(idx);
            if (version == _version) AddTo(to, step.Done ?? op);
            return new(true, $"{past} {what}" + (step.Note == null ? "" : ". " + step.Note));
        }
        if (step.Remainder == null) return new(false, $"Can't {verb} {what}: {step.Problem}"); // refused: entry stays, retry later
        if (idx >= 0) from[idx] = step.Remainder;
        if (version == _version && step.Done != null) AddTo(to, step.Done);
        return new(false, step.Done != null ? $"Only part of the {what} was {(forward ? "redone" : "undone")}. {step.Problem}"
                                            : $"Can't {verb} {what}: {step.Problem}");
    }

    private static void AddTo(List<UndoOp> list, UndoOp op)
    {
        list.Add(op);
        if (list.Count > Capacity) list.RemoveAt(0);
    }

    private static int IndexOfRef(List<UndoOp> list, UndoOp op)
    {
        for (int i = list.Count - 1; i >= 0; i--) if (ReferenceEquals(list[i], op)) return i;
        return -1;
    }

    // Everything is checked before anything is touched.
    private static async Task<Step> Apply(UndoOp op, bool forward, Func<IReadOnlyList<string>, bool> recycle,
        Func<string, bool?> confirm, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        switch (op)
        {
            case RenameOp r:
            {
                var (from, to) = forward ? (r.From, r.To) : (r.To, r.From);
                if (!Exists(from)) return Gone($"\"{Path.GetFileName(from)}\" was deleted, moved or renamed outside Frost.");
                bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && Exists(to)) return new($"\"{Path.GetFileName(to)}\" already exists.");
                if ((await ChangedAsync(new[] { from }, r.Print))[0])
                {
                    var a = confirm($"\"{Path.GetFileName(from)}\" changed since it was renamed (it may be a different item now). Rename it to \"{Path.GetFileName(to)}\" anyway?");
                    if (a == null) throw new OperationCanceledException();
                    if (a == false) return Gone("you chose to leave it as it is.");
                }
                FileOperations.Rename(from, Path.GetFileName(to));
                return new();
            }
            case CreateOp c:
                if (forward)
                {
                    if (Exists(c.Path)) return new($"\"{Path.GetFileName(c.Path)}\" already exists.");
                    if (c.IsDirectory) Directory.CreateDirectory(c.Path);
                    else using (new FileStream(c.Path, FileMode.CreateNew, FileAccess.Write)) { }
                    return new();
                }
                if (!Exists(c.Path)) return Gone($"\"{Path.GetFileName(c.Path)}\" was already deleted or moved outside Frost.");
                if ((await ChangedAsync(new[] { c.Path }, c.Print))[0])
                {
                    var a = confirm($"\"{Path.GetFileName(c.Path)}\" changed since it was created — move it to the Recycle Bin anyway?");
                    if (a == null) throw new OperationCanceledException();
                    if (a == false) return Gone("you chose to leave it in place.");
                }
                return !recycle(new[] { c.Path }) || Exists(c.Path) ? new("couldn't move it to the Recycle Bin.") : new();
            case TransferOp t:
                return !t.Move && !forward ? await UndoCopy(t, recycle, confirm) : await Transfer(t, forward, progress, ct);
            default: // RecycleOp is handled in Go and never reaches the redo stack
                throw new InvalidOperationException();
        }
    }

    private static TransferOp Sub(TransferOp t, IEnumerable<int> idx)
    {
        var l = idx.ToList();
        return t with { Items = l.Select(i => t.Items[i]).ToList(), Prints = Slice(t.Prints, l) };
    }

    private static async Task<Step> UndoCopy(TransferOp t, Func<IReadOnlyList<string>, bool> recycle, Func<string, bool?> confirm)
    {
        var alive = Enumerable.Range(0, t.Items.Count).Where(i => Exists(t.Items[i].Destination)).ToList();
        if (alive.Count == 0) return Gone("the copies were already deleted or moved outside Frost.");
        var changed = await ChangedAsync(alive.Select(i => t.Items[i].Destination).ToArray(), Slice(t.Prints, alive));
        var ok = new List<int>();
        var declined = new List<int>();
        for (int k = 0; k < alive.Count; k++)
        {
            string dst = t.Items[alive[k]].Destination;
            if (!changed[k]) { ok.Add(alive[k]); continue; }
            var a = confirm($"\"{Path.GetFileName(dst)}\" changed since it was copied — move it to the Recycle Bin anyway?");
            if (a == null) throw new OperationCanceledException(); // nothing touched yet
            (a == true ? ok : declined).Add(alive[k]);
        }
        if (ok.Count == 0) return Gone("you chose to leave the changed copy in place.");
        recycle(ok.Select(i => t.Items[i].Destination).ToArray());
        var recycled = ok.Where(i => !Exists(t.Items[i].Destination)).ToList();
        var failed = ok.Except(recycled).ToList();
        string? note = declined.Count > 0 ? $"Left in place (changed): {Names(declined.Select(i => t.Items[i].Destination))}." : null;
        if (failed.Count == 0) return new(Done: Sub(t, recycled), Note: note);
        string problem = $"Couldn't recycle: {Names(failed.Select(i => t.Items[i].Destination))}." + (note == null ? "" : " " + note);
        return new(problem, Sub(t, failed), recycled.Count > 0 ? Sub(t, recycled) : null);
    }

    private static async Task<Step> Transfer(TransferOp t, bool forward, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        // forward: Source→Destination; undoing a move: Destination→Source (work is parallel to t.Items)
        var work = forward ? t.Items : t.Items.Select(i => new TransferredItem(i.Destination, i.Source)).ToArray();
        var alive = Enumerable.Range(0, work.Count).Where(i => Exists(work[i].Source)).ToList();
        if (alive.Count == 0) return Gone("the items were deleted or moved outside Frost.");
        foreach (int k in alive)
        {
            var i = work[k];
            if (Exists(i.Destination)) return new($"\"{Path.GetFileName(i.Destination)}\" already exists in {Path.GetDirectoryName(i.Destination)}.");
            if (!Directory.Exists(Path.GetDirectoryName(i.Destination))) return new($"The folder {Path.GetDirectoryName(i.Destination)} no longer exists.");
        }
        var done = new List<TransferredItem>();
        string? error = null;
        try { await FileOperations.TransferExactAsync(alive.Select(k => work[k]).ToList(), t.Move, progress, ct, done); }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException)
        { error = e is OperationCanceledException ? "cancelled" : e.Message; }

        var doneIdx = alive.Where(k => done.Contains(work[k])).ToList();
        var restIdx = alive.Except(doneIdx).ToList();
        string? note = alive.Count < work.Count ? $"{work.Count - alive.Count} item(s) were already gone." : null;
        if (restIdx.Count == 0) return new(Done: alive.Count == work.Count ? null : Sub(t, doneIdx), Note: note);
        string why = error ?? "skipped because the target appeared meanwhile";
        if (doneIdx.Count == 0) return new($"nothing was done ({why}).");
        return new($"Done: {Names(doneIdx.Select(i => work[i].Source))}. Not done: {Names(restIdx.Select(i => work[i].Source))} ({why}).",
            Sub(t, restIdx), Sub(t, doneIdx));
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static string Names(IEnumerable<string> paths)
    {
        var n = paths.Select(p => $"\"{Path.GetFileName(p)}\"").ToList();
        return n.Count <= 3 ? string.Join(", ", n) : string.Join(", ", n.Take(3)) + $" and {n.Count - 3} more";
    }

    private static string Describe(UndoOp op)
    {
        static string Items(IReadOnlyList<string> names) => names.Count == 1 ? $"\"{names[0]}\"" : $"{names.Count} items";
        return op switch
        {
            RenameOp r => $"rename of \"{Path.GetFileName(r.To)}\"",
            CreateOp c => $"creation of \"{Path.GetFileName(c.Path)}\"",
            TransferOp t => $"{(t.Move ? "move" : "copy")} of {Items(t.Items.Select(i => Path.GetFileName(i.Destination)).ToArray())}",
            RecycleOp d => $"delete of {Items(d.Paths.Select(Path.GetFileName).ToArray()!)}",
            _ => "operation",
        };
    }
}
