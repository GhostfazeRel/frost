namespace Frost.Core;

public abstract record UndoOp;
public sealed record RenameOp(string From, string To, string? Print = null) : UndoOp;
public sealed record CreateOp(string Path, bool IsDirectory, string? Print = null) : UndoOp;
public sealed record TransferOp(bool Move, IReadOnlyList<TransferredItem> Items) : UndoOp;
public sealed record RecycleOp(IReadOnlyList<string> Paths) : UndoOp;

/// <param name="Message">What happened, or why it was refused.</param>
/// <param name="CanOpenRecycleBin">The App should offer an "Open Recycle Bin" button (deletes can't be undone by Frost).</param>
public sealed record UndoOutcome(bool Ok, string Message, bool CanOpenRecycleBin = false);

/// <summary>"Is this still the item we made?" fingerprints. Files: length + last write time. Folders: every entry's
/// relative path, length and mtime. Null when the item is missing, unreadable, or a folder has more than MaxEntries
/// entries (unverifiable).</summary>
public static class ItemPrint
{
    public const int MaxEntries = 5000;

    public static string? Of(string path)
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
                if (entries.Count >= MaxEntries) return null;
                string rel = Path.GetRelativePath(root, e.FullName);
                entries.Add(e is FileInfo fi ? $"{rel}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : rel + "|d");
            }
            entries.Sort(StringComparer.Ordinal);
            return "D|" + string.Join("\n", entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True unless the item still matches the recorded fingerprint (unverifiable counts as changed).</summary>
    public static bool Changed(string path, string? recorded)
    {
        var now = Of(path);
        return now == null || recorded == null || now != recorded;
    }
}

/// <summary>Reversible file operations, recorded as data. Undo/Redo check that every path is still in the expected
/// state and refuse (never overwrite) otherwise; copies/created items that changed since are only recycled after
/// `confirm` says yes. A refused undo keeps its entry (retry after fixing things); a partly applied one keeps the
/// remainder and reports exactly what was and wasn't done. Use from the UI thread only.</summary>
public sealed class UndoHistory
{
    public const int Capacity = 50;
    private readonly List<UndoOp> _undo = new(), _redo = new();
    private long _version; // bumped by every Push so a running undo/redo can tell a new operation happened meanwhile

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Short phrase for menus, e.g. "rename of \"a.txt\"", or null when empty.</summary>
    public string? UndoLabel => _undo.Count > 0 ? Describe(_undo[^1]) : null;
    public string? RedoLabel => _redo.Count > 0 ? Describe(_redo[^1]) : null;
    public int UndoCount => _undo.Count;

    /// <summary>Call right AFTER renaming (fingerprints the item at `to`).</summary>
    public void RecordRename(string from, string to) { if (from != to) Push(new RenameOp(from, to, ItemPrint.Of(to))); }
    /// <summary>Call right AFTER creating the (empty) item.</summary>
    public void RecordCreate(string path, bool isDirectory) => Push(new CreateOp(path, isDirectory, ItemPrint.Of(path)));
    /// <summary>Call right AFTER the transfer finished (fingerprints the copies).</summary>
    public void RecordTransfer(bool move, IEnumerable<TransferredItem> items)
    {
        var list = items.Select(i => move ? i : i with { Print = ItemPrint.Of(i.Destination) }).ToArray();
        if (list.Length > 0) Push(new TransferOp(move, list));
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

    /// <summary>Reverses the newest operation. `recycle` sends paths to the Recycle Bin and returns true on success;
    /// `confirm` asks a yes/no question (default should be No).</summary>
    public Task<UndoOutcome> UndoAsync(Func<IReadOnlyList<string>, bool> recycle, Func<string, bool> confirm,
        IProgress<OperationProgress>? progress, CancellationToken ct) => Go(false, recycle, confirm, progress, ct);

    public Task<UndoOutcome> RedoAsync(Func<IReadOnlyList<string>, bool> recycle, Func<string, bool> confirm,
        IProgress<OperationProgress>? progress, CancellationToken ct) => Go(true, recycle, confirm, progress, ct);

    private sealed record Step(string? Problem = null, UndoOp? Remainder = null, UndoOp? Done = null);

    private async Task<UndoOutcome> Go(bool forward, Func<IReadOnlyList<string>, bool> recycle, Func<string, bool> confirm,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var from = forward ? _redo : _undo;
        var to = forward ? _undo : _redo;
        string verb = forward ? "redo" : "undo", past = forward ? "Redid" : "Undid";
        if (from.Count == 0) return new(false, $"Nothing to {verb}.");
        var op = from[^1];
        string what = Describe(op);
        // Restoring from the Recycle Bin can't be done reliably without the Windows shell, so offer to open it instead.
        if (op is RecycleOp d)
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
        if (step.Problem == null)
        {
            if (idx >= 0) from.RemoveAt(idx);
            if (version == _version) AddTo(to, op);
            return new(true, $"{past} {what}");
        }
        if (step.Remainder == null) return new(false, $"Can't {verb} {what}: {step.Problem}"); // refused: entry stays, retry later
        if (idx >= 0) from[idx] = step.Remainder;
        if (version == _version && step.Done != null) AddTo(to, step.Done);
        return new(false, $"Only part of the {what} was {(forward ? "redone" : "undone")}. {step.Problem}");
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

    // Everything is checked before anything is touched. Returns Problem=null on full success.
    private static async Task<Step> Apply(UndoOp op, bool forward, Func<IReadOnlyList<string>, bool> recycle,
        Func<string, bool> confirm, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        switch (op)
        {
            case RenameOp r:
            {
                var (from, to) = forward ? (r.From, r.To) : (r.To, r.From);
                if (!Exists(from)) return new($"\"{Path.GetFileName(from)}\" no longer exists.");
                bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && Exists(to)) return new($"\"{Path.GetFileName(to)}\" already exists.");
                if (ItemPrint.Changed(from, r.Print) &&
                    !confirm($"\"{Path.GetFileName(from)}\" changed since it was renamed (it may be a different item now). Rename it to \"{Path.GetFileName(to)}\" anyway?"))
                    return new("left as it is.");
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
                if (!Exists(c.Path)) return new($"\"{Path.GetFileName(c.Path)}\" is already gone.");
                if (ItemPrint.Changed(c.Path, c.Print) &&
                    !confirm($"\"{Path.GetFileName(c.Path)}\" changed since it was created — move it to the Recycle Bin anyway?"))
                    return new("left in place.");
                return !recycle(new[] { c.Path }) || Exists(c.Path) ? new("couldn't move it to the Recycle Bin.") : new();
            case TransferOp t:
                return !t.Move && !forward ? UndoCopy(t, recycle, confirm) : await Transfer(t, forward, progress, ct);
            default: // RecycleOp is handled in Go and never reaches the redo stack
                throw new InvalidOperationException();
        }
    }

    private static Step UndoCopy(TransferOp t, Func<IReadOnlyList<string>, bool> recycle, Func<string, bool> confirm)
    {
        foreach (var i in t.Items)
            if (!Exists(i.Destination)) return new($"\"{Path.GetFileName(i.Destination)}\" is already gone.");
        var ok = new List<TransferredItem>();
        var left = new List<TransferredItem>();
        foreach (var i in t.Items)
        {
            if (ItemPrint.Changed(i.Destination, i.Print) &&
                !confirm($"\"{Path.GetFileName(i.Destination)}\" changed since it was copied — move it to the Recycle Bin anyway?"))
                left.Add(i);
            else ok.Add(i);
        }
        if (ok.Count == 0) return new("left in place (changed since it was copied).");
        recycle(ok.Select(i => i.Destination).ToArray());
        var gone = ok.Where(i => !Exists(i.Destination)).ToList();
        left.AddRange(ok.Where(i => Exists(i.Destination)));
        if (left.Count == 0) return new();
        if (gone.Count == 0) return new("couldn't move the items to the Recycle Bin.");
        return new($"Recycled: {Names(gone.Select(i => i.Destination))}. Left in place: {Names(left.Select(i => i.Destination))}.",
            t with { Items = left }, t with { Items = gone });
    }

    private static async Task<Step> Transfer(TransferOp t, bool forward, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        // forward: Source→Destination; undoing a move: Destination→Source
        var work = forward ? t.Items : t.Items.Select(i => new TransferredItem(i.Destination, i.Source)).ToArray();
        foreach (var i in work)
        {
            if (!Exists(i.Source)) return new($"\"{Path.GetFileName(i.Source)}\" is no longer at {Path.GetDirectoryName(i.Source)}.");
            if (Exists(i.Destination)) return new($"\"{Path.GetFileName(i.Destination)}\" already exists in {Path.GetDirectoryName(i.Destination)}.");
            if (!Directory.Exists(Path.GetDirectoryName(i.Destination))) return new($"The folder {Path.GetDirectoryName(i.Destination)} no longer exists.");
        }
        var done = new List<TransferredItem>();
        string? error = null;
        try { await FileOperations.TransferExactAsync(work, t.Move, progress, ct, done); }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException)
        { error = e is OperationCanceledException ? "cancelled" : e.Message; }

        var doneIdx = Enumerable.Range(0, work.Count).Where(i => done.Contains(work[i])).ToList();
        if (doneIdx.Count == work.Count) return new();
        var restIdx = Enumerable.Range(0, work.Count).Except(doneIdx).ToList();
        string why = error ?? "skipped because the target appeared meanwhile";
        if (doneIdx.Count == 0) return new($"nothing was done ({why}).");
        return new($"Done: {Names(doneIdx.Select(i => work[i].Source))}. Not done: {Names(restIdx.Select(i => work[i].Source))} ({why}).",
            t with { Items = restIdx.Select(i => t.Items[i]).ToList() }, t with { Items = doneIdx.Select(i => t.Items[i]).ToList() });
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
