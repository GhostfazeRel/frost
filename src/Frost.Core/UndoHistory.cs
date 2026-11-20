namespace Frost.Core;

public abstract record UndoOp;
public sealed record RenameOp(string From, string To) : UndoOp;
public sealed record CreateOp(string Path, bool IsDirectory) : UndoOp;
public sealed record TransferOp(bool Move, IReadOnlyList<TransferredItem> Items) : UndoOp;
public sealed record RecycleOp(IReadOnlyList<string> Paths) : UndoOp;

/// <param name="Message">What happened, or why it was refused.</param>
/// <param name="CanOpenRecycleBin">The App should offer an "Open Recycle Bin" button (deletes can't be undone by Frost).</param>
public sealed record UndoOutcome(bool Ok, string Message, bool CanOpenRecycleBin = false);

/// <summary>Reversible file operations, recorded as data. Undo/Redo check that every path is still in the expected
/// state and refuse (never overwrite) otherwise. A refused or failed entry is dropped so it can't block older ones.
/// Not thread-safe: use from the UI thread only.</summary>
public sealed class UndoHistory
{
    public const int Capacity = 50;
    private readonly List<UndoOp> _undo = new(), _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Short phrase for menus, e.g. "rename of \"a.txt\"", or null when empty.</summary>
    public string? UndoLabel => _undo.Count > 0 ? Describe(_undo[^1]) : null;
    public string? RedoLabel => _redo.Count > 0 ? Describe(_redo[^1]) : null;
    public int UndoCount => _undo.Count;

    public void RecordRename(string from, string to) { if (from != to) Push(new RenameOp(from, to)); }
    public void RecordCreate(string path, bool isDirectory) => Push(new CreateOp(path, isDirectory));
    public void RecordTransfer(bool move, IEnumerable<TransferredItem> items)
    {
        var list = items.ToArray();
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
    }

    /// <summary>Reverses the newest operation. `recycle` sends paths to the Recycle Bin and returns true on success.</summary>
    public async Task<UndoOutcome> UndoAsync(Func<IReadOnlyList<string>, bool> recycle,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (_undo.Count == 0) return new(false, "Nothing to undo.");
        var op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        // Restoring from the Recycle Bin can't be done reliably without the Windows shell, so offer to open it instead.
        if (op is RecycleOp d)
            return new(false, $"Can't undo {Describe(d)} — restore the items from the Recycle Bin.", CanOpenRecycleBin: true);
        var r = await Run(op, forward: false, recycle, progress, ct);
        if (r.Ok) _redo.Add(op);
        return r;
    }

    public async Task<UndoOutcome> RedoAsync(Func<IReadOnlyList<string>, bool> recycle,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (_redo.Count == 0) return new(false, "Nothing to redo.");
        var op = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        var r = await Run(op, forward: true, recycle, progress, ct);
        if (r.Ok) _undo.Add(op);
        return r;
    }

    private static async Task<UndoOutcome> Run(UndoOp op, bool forward, Func<IReadOnlyList<string>, bool> recycle,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        string what = Describe(op), verb = forward ? "Redid" : "Undid";
        try
        {
            string? problem = await Apply(op, forward, recycle, progress, ct);
            return problem == null ? new(true, $"{verb} {what}")
                                   : new(false, $"Can't {(forward ? "redo" : "undo")} {what}: {problem}");
        }
        catch (OperationCanceledException) { return new(false, $"{(forward ? "Redo" : "Undo")} cancelled."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"Can't {(forward ? "redo" : "undo")} {what}: {e.Message}");
        }
    }

    // Returns null on success, else the reason it was refused. Everything is checked before anything is touched.
    private static async Task<string?> Apply(UndoOp op, bool forward, Func<IReadOnlyList<string>, bool> recycle,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        switch (op)
        {
            case RenameOp r:
            {
                var (from, to) = forward ? (r.From, r.To) : (r.To, r.From);
                if (!Exists(from)) return $"\"{Path.GetFileName(from)}\" no longer exists.";
                bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && Exists(to)) return $"\"{Path.GetFileName(to)}\" already exists.";
                FileOperations.Rename(from, Path.GetFileName(to));
                return null;
            }
            case CreateOp c:
                if (forward)
                {
                    if (Exists(c.Path)) return $"\"{Path.GetFileName(c.Path)}\" already exists.";
                    if (c.IsDirectory) Directory.CreateDirectory(c.Path);
                    else using (new FileStream(c.Path, FileMode.CreateNew, FileAccess.Write)) { }
                    return null;
                }
                return Recycle(new[] { c.Path }, recycle);
            case TransferOp t:
                if (!t.Move && !forward) return Recycle(t.Items.Select(i => i.Destination).ToArray(), recycle);
                // forward copy/move: Source→Destination; undo of a move: Destination→Source
                var items = forward ? t.Items : t.Items.Select(i => new TransferredItem(i.Destination, i.Source)).ToArray();
                foreach (var i in items)
                {
                    if (!Exists(i.Source)) return $"\"{Path.GetFileName(i.Source)}\" is no longer at {Path.GetDirectoryName(i.Source)}.";
                    if (Exists(i.Destination)) return $"\"{Path.GetFileName(i.Destination)}\" already exists in {Path.GetDirectoryName(i.Destination)}.";
                }
                await FileOperations.TransferExactAsync(items, t.Move, progress, ct);
                return null;
            default: // RecycleOp is handled in UndoAsync and never reaches the redo stack
                throw new InvalidOperationException();
        }
    }

    private static string? Recycle(IReadOnlyList<string> paths, Func<IReadOnlyList<string>, bool> recycle)
    {
        foreach (var p in paths)
            if (!Exists(p)) return $"\"{Path.GetFileName(p)}\" is already gone.";
        if (!recycle(paths) || paths.Any(Exists)) return "couldn't move the items to the Recycle Bin.";
        return null;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

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
