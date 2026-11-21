using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Frost.Core;
namespace Frost.Linux;

/// <summary>One running operation in the panel at the bottom (port of OperationViewModel).</summary>
sealed class Op
{
    readonly Stopwatch _sw = Stopwatch.StartNew();
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _item = new() { Text = "Preparing…", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis }, _detail = new() { Classes = { "dim" } };
    public CancellationTokenSource Cts { get; } = new();
    public Border Row { get; }

    public Op(string title)
    {
        var cancel = new Button { Content = "Cancel", [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center };
        cancel.Click += (_, _) => Cts.Cancel();
        var left = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold }, _item, _bar, _detail } };
        Row = new Border { Classes = { "card" }, Padding = new Thickness(12, 8), Child = new Grid { ColumnDefinitions = new("*,Auto"), Children = { left, cancel } } };
    }

    public void Report(OperationProgress p)
    {
        _item.Text = p.CurrentItem;
        _bar.Value = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : p.ItemsTotal > 0 ? 100.0 * p.ItemsDone / p.ItemsTotal : 0;
        var secs = _sw.Elapsed.TotalSeconds;
        if (secs < 0.5 || p.BytesDone <= 0) { _detail.Text = $"{p.ItemsDone} of {p.ItemsTotal} items"; return; }
        var speed = p.BytesDone / secs;
        var left = TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed);
        _detail.Text = $"{Format.Size((long)speed)}/s · " + (left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : left.TotalMinutes >= 1 ? $"{left.Minutes} min {left.Seconds} s" : $"{Math.Max(1, left.Seconds)} s") + " left";
    }
}

public partial class MainWindow
{
    readonly UndoHistory _history = new();
    List<string> _clip = new(); // fallback when the system clipboard has no files
    // What GNOME/KDE file managers put on the clipboard: "copy|cut\nfile:///…" and a plain URI list.
    static readonly DataFormat<byte[]> GnomeFiles = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");
    static readonly DataFormat<byte[]> UriList = DataFormat.CreateBytesPlatformFormat("text/uri-list");
    bool _clipCut, _undoBusy;
    bool HistoryBusy => _undoBusy || Ops.Children.Count > 0; // undo/redo are off while a copy/move or another undo runs

    Op AddOp(string title) { var op = new Op(title); Ops.Children.Add(op.Row); return op; }

    // ================= clipboard =================
    void CopySelection(bool cut)
    {
        var p = Sel.Select(i => i.FullPath).ToList();
        if (p.Count == 0) return;
        (_clip, _clipCut) = (p, cut);
        _ = ExportClipboard(p, cut);
        Notify(cut ? $"Cut {p.Count} item(s)" : $"Copied {p.Count} item(s)");
    }

    async Task ExportClipboard(List<string> paths, bool cut)
    {
        if (Clipboard == null) return;
        var uris = paths.Select(p => new Uri(p).AbsoluteUri).ToList();
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(GnomeFiles, System.Text.Encoding.UTF8.GetBytes((cut ? "cut\n" : "copy\n") + string.Join("\n", uris))));
        data.Add(DataTransferItem.Create(UriList, System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n")));
        data.Add(DataTransferItem.Create(DataFormat.Text, string.Join("\n", paths)));
        try { await Clipboard.SetDataAsync(data); } catch (Exception ex) { Console.Error.WriteLine("clipboard export failed: " + ex); } // the in-app copy still works
    }

    /// <summary>Files on the system clipboard (from Frost or another file manager), or null.</summary>
    async Task<(List<string> Paths, bool Cut)?> ImportClipboard()
    {
        if (Clipboard == null) return null;
        try
        {
            using var data = await Clipboard.TryGetDataAsync();
            if (data == null) return null;
            if (await data.TryGetValueAsync(GnomeFiles) is { } g)
            {
                var lines = System.Text.Encoding.UTF8.GetString(g).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var files = FromUris(lines.Skip(1));
                if (lines.Length > 1 && files.Count > 0) return (files, lines[0] == "cut");
            }
            if (await data.TryGetValueAsync(UriList) is { } u)
            {
                var files = FromUris(System.Text.Encoding.UTF8.GetString(u).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#')));
                if (files.Count > 0) return (files, false);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("clipboard import failed: " + ex); }
        return null;
    }

    static List<string> FromUris(IEnumerable<string> uris) =>
        uris.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.IsFile ? x.LocalPath : null).OfType<string>().ToList();

    void CopyPath()
    {
        var p = Sel.Select(i => i.FullPath).ToList();
        var text = p.Count > 0 ? string.Join("\n", p) : _cur?.Path;
        if (string.IsNullOrEmpty(text)) return;
        _ = Clipboard?.SetTextAsync(text);
        Notify("Path copied");
    }

    async void Paste(string? dest = null)
    {
        dest ??= _cur?.Path;
        if (dest == null) return;
        var (files, cut) = await ImportClipboard() is { } sys ? sys : (_clip, _clipCut);
        if (files.Count == 0) return;
        if (cut) { _clip = new(); try { if (Clipboard != null) await Clipboard.ClearAsync(); } catch { } } // a cut pastes once
        await TransferAsync(files, dest, cut);
    }

    // ================= transfers =================
    async Task TransferAsync(IReadOnlyList<string> sources, string dest, bool move)
    {
        var op = AddOp(move ? "Moving" : "Copying");
        var progress = new Progress<OperationProgress>(op.Report); // created on the UI thread so Report marshals back
        ConflictChoice? all = null;
        var done = new List<TransferredItem>();
        ConflictChoice OnConflict(string target) // runs on the worker thread; blocks it while the dialog is open
        {
            if (all != null) return all.Value;
            var (idx, applyAll) = Dispatcher.UIThread.InvokeAsync<(int, bool)>(() => Dialogs.Ask(this, "File already exists",
                $"\"{Path.GetFileName(target)}\" already exists in the destination folder.", new[] { "Replace", "Skip", "Keep both" }, "Do this for all conflicts")).GetAwaiter().GetResult();
            var c = idx switch { 0 => ConflictChoice.Replace, 2 => ConflictChoice.KeepBoth, _ => ConflictChoice.Skip };
            if (applyAll) all = c;
            return c;
        }
        try { await FileOperations.TransferAsync(sources, dest, move, OnConflict, progress, op.Cts.Token, done); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify((move ? "Move" : "Copy") + " failed: " + ex.Message); }
        finally
        {
            Ops.Children.Remove(op.Row);
            _history.RecordTransfer(move, done);
            foreach (var t in _tabs)
                if (t.Loaded && (Platform.Same(t.Path, dest) || sources.Any(s => Platform.Same(Path.GetDirectoryName(s), t.Path)))) _ = t.RefreshAsync();
        }
    }

    // ================= delete / create / rename =================
    async void DeleteSelection(bool permanent)
    {
        var t = _cur;
        var paths = Sel.Select(i => i.FullPath).ToList();
        if (t == null || paths.Count == 0) return;
        try
        {
            if (permanent)
            {
                var msg = paths.Count == 1 ? $"Permanently delete \"{Path.GetFileName(paths[0])}\"? This can't be undone." : $"Permanently delete these {paths.Count} items? This can't be undone.";
                if ((await Dialogs.Ask(this, "Delete permanently", msg, new[] { "Delete", "Cancel" })).Index != 0) return;
                await Task.Run(() => FileOperations.DeletePermanently(paths));
            }
            else if (await Task.Run(() => Platform.Trash(paths))) _history.RecordRecycle(paths.Where(x => !File.Exists(x) && !Directory.Exists(x)));
            else Notify("Couldn't move to the trash (is gio installed, and does this drive have a trash?). Nothing was deleted. Shift+Delete deletes permanently.");
        }
        catch (Exception ex) { Notify("Delete failed: " + ex.Message); }
        _ = t.RefreshAsync();
    }

    async void NewFolder()
    {
        var t = _cur;
        if (t == null || t.SearchQuery != null) return;
        try { var p = FileOperations.CreateFolder(t.Path); _history.RecordCreate(p, true); await t.RefreshAsync(p); await RenamePrompt(p, true); }
        catch (Exception ex) { Notify("Couldn't create folder: " + ex.Message); }
    }

    async void NewTextFile()
    {
        var t = _cur;
        if (t == null || t.SearchQuery != null) return;
        try { var p = FileOperations.CreateFile(t.Path, "New Text Document.txt"); _history.RecordCreate(p, false); await t.RefreshAsync(p); await RenamePrompt(p, false); }
        catch (Exception ex) { Notify("Couldn't create file: " + ex.Message); }
    }

    async Task RenamePrompt(string path, bool isDir)
    {
        var old = Path.GetFileName(path.TrimEnd('/'));
        var name = (await Dialogs.Prompt(this, "Rename", old, isDir))?.Trim();
        if (string.IsNullOrEmpty(name) || name == old) return;
        if (!PathUtil.IsValidFileName(name)) { Notify("A file name can't be empty, end with a dot or contain  \\ / : * ? \" < > |"); return; }
        try { var np = FileOperations.Rename(path, name); _history.RecordRename(path, np); if (_cur != null) await _cur.RefreshAsync(np); } // refuses when the name exists
        catch (Exception ex) { Notify("Rename failed: " + ex.Message); }
    }

    /// <summary>F2: a prompt for one item, the batch dialog for several.</summary>
    async void RenameSelection()
    {
        var t = _cur;
        if (t == null || Sel.Count == 0) return;
        if (Sel.Count == 1) { await RenamePrompt(Sel[0].FullPath, Sel[0].IsDirectory); return; }
        var order = t.Items.Select((it, i) => (it, i)).ToDictionary(x => x.it, x => x.i); // {n} numbering follows the list order
        var paths = Sel.OrderBy(i => order.GetValueOrDefault(i, int.MaxValue)).Select(i => i.FullPath).ToList();
        var applied = await Dialogs.BatchRename(this, paths);
        if (applied.Count == 0) return;
        _history.RecordBatchRename(applied);
        try { await t.RefreshAsync(); } catch (Exception ex) { Notify(ex.Message); }
        Notify($"Renamed {applied.Count} items.");
    }

    // ================= undo / redo =================
    IEnumerable<Avalonia.Controls.Control> UndoItems()
    {
        yield return MI(_history.CanUndo ? $"Undo {_history.UndoLabel}" : "Undo", () => _ = RunUndo(false), "Ctrl+Z", _history.CanUndo && !HistoryBusy);
        yield return MI(_history.CanRedo ? $"Redo {_history.RedoLabel}" : "Redo", () => _ = RunUndo(true), "Ctrl+Y", _history.CanRedo && !HistoryBusy);
    }

    async Task RunUndo(bool redo)
    {
        if (HistoryBusy) { Notify("Wait for the current operation to finish."); return; }
        _undoBusy = true;
        var op = AddOp(redo ? "Redoing" : "Undoing");
        try
        {
            var progress = new Progress<OperationProgress>(op.Report);
            // "Leave it" = false (entry dropped), "Continue anyway" = true, Cancel / Esc = null (abort the whole undo). Runs on the worker thread.
            bool? Confirm(string msg) => Dispatcher.UIThread.InvokeAsync<(int, bool)>(() => Dialogs.Ask(this, "Changed since", msg, new[] { "Leave it", "Continue anyway", "Cancel" }))
                .GetAwaiter().GetResult().Item1 switch { 0 => false, 1 => true, _ => null };
            // Run on the thread pool: Core calls the callbacks synchronously and they block on UI dialogs.
            var r = await Task.Run(() => redo ? _history.RedoAsync(Platform.Trash, Confirm, progress, op.Cts.Token) : _history.UndoAsync(Platform.Trash, Confirm, progress, op.Cts.Token));
            if (r.CanOpenRecycleBin)
            {
                if ((await Dialogs.Ask(this, "Can't undo delete", r.Message.Replace("Recycle Bin", "trash"), new[] { "Open trash", "Close" })).Index == 0) Platform.OpenTrash();
            }
            else Notify(r.Message.Replace("Recycle Bin", "trash"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify((redo ? "Redo" : "Undo") + " failed: " + ex.Message); }
        finally
        {
            Ops.Children.Remove(op.Row);
            _undoBusy = false;
            foreach (var t in _tabs) if (t.Loaded) _ = t.RefreshAsync();
        }
    }

    // ================= ZIP =================
    void CompressSelection()
    {
        var t = _cur;
        if (t == null || Sel.Count == 0) return;
        var paths = Sel.Select(i => i.FullPath).ToList();
        var zip = Archives.ZipPathFor(paths, t.Path);
        _ = RunArchiveOp("Compressing", Path.GetDirectoryName(zip)!, async (p, ct) => (await Archives.CompressAsync(paths, zip, p, ct), zip));
    }

    void ExtractZip(Item zipItem)
    {
        var parent = _cur?.Path ?? Path.GetDirectoryName(zipItem.FullPath)!;
        var dest = Archives.ExtractFolderFor(zipItem.FullPath, parent);
        _ = RunArchiveOp("Extracting", parent, async (p, ct) => (0, await Archives.ExtractAsync(zipItem.FullPath, dest, p, ct)));
    }

    async Task RunArchiveOp(string title, string outputParent, Func<IProgress<OperationProgress>, CancellationToken, Task<(int Skipped, string Output)>> work)
    {
        string? output = null;
        var op = AddOp(title);
        var progress = new Progress<OperationProgress>(op.Report);
        try
        {
            var (skipped, final) = await Task.Run(() => work(progress, op.Cts.Token));
            output = final;
            _history.RecordCreate(final, Directory.Exists(final), redoable: false);
            if (skipped > 0) Notify($"Done. Skipped {skipped} linked folder(s) or broken link(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify(title + " failed: " + ex.Message); }
        finally
        {
            Ops.Children.Remove(op.Row);
            foreach (var t in _tabs)
                if (t.Loaded && Platform.Same(t.Path, outputParent)) _ = t.RefreshAsync(output != null && (File.Exists(output) || Directory.Exists(output)) ? output : null);
        }
    }

    // ================= command palette / go to folder =================
    record Cmd(string Title, string Gesture, Action Run);

    List<Cmd> BuildCommands()
    {
        var t = _cur;
        var cmds = new List<Cmd>
        {
            new("New tab", "Ctrl+T", () => NewTab()),
            new("Close tab", "Ctrl+W", () => CloseTab(_cur)),
            new("Close other tabs", "", () => { var keep = _cur; foreach (var x in _tabs.Where(x => x != keep).ToList()) CloseTab(x); }),
            new("Reopen closed tab", "Ctrl+Shift+T", ReopenClosedTab),
            new("Duplicate tab", "", () => { if (_cur != null) { AddTab(_cur.Path, _tabs.IndexOf(_cur) + 1); Activate(_tabs[_tabs.IndexOf(_cur) + 1]); } }),
            new("Next tab", "Ctrl+Tab", () => CycleTab(1)),
            new("Previous tab", "Ctrl+Shift+Tab", () => CycleTab(-1)),
            new("Go to folder", "Ctrl+P", ShowGoToFolder),
            new("Back", "Alt+Left", () => _cur?.GoBack()),
            new("Forward", "Alt+Right", () => _cur?.GoForward()),
            new("Up one level", "Alt+Up", () => _cur?.Up()),
            new("Refresh", "F5", Refresh),
            new("Edit address bar", "Ctrl+L", BeginAddressEdit),
            new("Search", "Ctrl+F", () => { SearchBox.Focus(); SearchBox.SelectAll(); }),
            new("New folder", "Ctrl+Shift+N", NewFolder),
            new("New text document", "", NewTextFile),
            new("Copy", "Ctrl+C", () => CopySelection(false)),
            new("Cut", "Ctrl+X", () => CopySelection(true)),
            new("Paste", "Ctrl+V", () => Paste()),
            new("Select all", "Ctrl+A", () => List.SelectAll()),
            new("Rename", "F2", RenameSelection),
            new("Move to trash", "Delete", () => DeleteSelection(false)),
            new("Delete permanently", "Shift+Delete", () => DeleteSelection(true)),
            new("Copy path", "Ctrl+Shift+C", CopyPath),
            new("Open terminal here", "", () => OpenTerminal(null)),
            new("Open trash", "", Platform.OpenTrash),
            new("View: Details", "Ctrl+Shift+1", () => SetView(ViewMode.Details)),
            new("View: Tiles", "Ctrl+Shift+2", () => SetView(ViewMode.Tiles)),
            new("View: Large icons", "Ctrl+Shift+3", () => SetView(ViewMode.Large)),
            new("View: Extra large icons", "Ctrl+Shift+4", () => SetView(ViewMode.ExtraLarge)),
            new("Sort by name", "", () => { _s.SortColumn = SortColumn.Name; ResortAll(); }),
            new("Sort by date modified", "", () => { _s.SortColumn = SortColumn.Modified; ResortAll(); }),
            new("Sort by type", "", () => { _s.SortColumn = SortColumn.Type; ResortAll(); }),
            new("Sort by size", "", () => { _s.SortColumn = SortColumn.Size; ResortAll(); }),
            new("Sort ascending", "", () => { _s.SortDescending = false; ResortAll(); }),
            new("Sort descending", "", () => { _s.SortDescending = true; ResortAll(); }),
            new(_s.ShowHidden ? "Hide hidden items" : "Show hidden items", "Ctrl+H", ToggleHidden),
            new(_s.PreviewPane ? "Hide preview pane" : "Show preview pane", "Alt+P", TogglePreview),
            new(t != null && IsPinned(t.Path) ? "Unpin this folder from sidebar" : "Pin this folder to sidebar", "", () => { if (_cur != null) TogglePin(_cur.Path); }),
            new("Undo", "Ctrl+Z", () => _ = RunUndo(false)),
            new("Redo", "Ctrl+Y", () => _ = RunUndo(true)),
        };
        if (t != null && t.Selection.Any(i => i.IsDirectory))
            cmds.Add(new("Open selection in new tab", "", () => { foreach (var i in Sel.Where(i => i.IsDirectory).ToList()) NewTab(i.FullPath, activate: false); }));
        if (Sel.Count > 1) cmds.Add(new("Batch rename", "F2", RenameSelection));
        if (Sel.Count > 0) cmds.Add(new("Compress to ZIP", "", CompressSelection));
        if (Sel.Count == 1 && Archives.IsZip(Sel[0].Name)) cmds.Add(new("Extract here", "", () => ExtractZip(Sel[0])));
        return cmds;
    }

    void ShowCommandPalette()
    {
        var cmds = BuildCommands();
        Dialogs.Palette(this, "Type a command…", q => Fuzzy.Rank(cmds, q, c => c.Title).Select(r => new Dialogs.Row(r.Item.Title, r.Item.Gesture, r.Item.Run, r.Match.Indices)).ToList());
    }

    void ShowGoToFolder()
    {
        var list = new List<(string Path, string Kind)>();
        var seen = new HashSet<string>();
        void Add(string? p, string kind) { if (!string.IsNullOrEmpty(p) && seen.Add(p.Length > 1 ? p.TrimEnd('/') : p)) list.Add((p, kind)); }
        foreach (var p in _s.RecentFolders) Add(p, "Recent");
        foreach (var p in _s.Pinned) Add(p, "Pinned");
        foreach (var (_, p) in Platform.QuickAccess()) Add(p, "Quick access");
        foreach (var (_, p, _) in Platform.Drives()) Add(p, "Drive");
        if (_cur != null) foreach (var i in _cur.Items.Where(i => i.IsDirectory).Take(300)) Add(i.FullPath, "Subfolder");
        Dialogs.Palette(this, "Go to folder… (type a name or a path)", q =>
        {
            var rows = new List<Dialogs.Row>();
            var typed = q.Trim();
            if (typed.StartsWith('~')) typed = Platform.Home + typed[1..];
            if (typed.StartsWith('/') && Directory.Exists(typed)) rows.Add(new(typed, "Open", () => _cur?.Navigate(typed), Array.Empty<int>()));
            rows.AddRange(Fuzzy.Rank(list, q, x => x.Path).Select(r => new Dialogs.Row(r.Item.Path, r.Item.Kind, () => _cur?.Navigate(r.Item.Path), r.Match.Indices)));
            return rows;
        });
    }
}

public partial class MainWindow
{
    // ================= drag and drop =================
    // In-app paths travel in our own format; file lists from other apps arrive as DataFormats.Files.
    public static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("x-frost-paths");
    Point? _dragStart;
    PointerPressedEventArgs? _dragPress; // Avalonia 12 starts a drag from the press, not the move

    void SetUpDragDrop()
    {
        List.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var start = e.GetCurrentPoint(List).Properties.IsLeftButtonPressed && ItemAt(e.Source) != null;
            _dragStart = start ? e.GetPosition(List) : null;
            _dragPress = start ? e : null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        List.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_dragStart is not { } start || _dragPress is not { } press || !e.GetCurrentPoint(List).Properties.IsLeftButtonPressed) return;
            var d = e.GetPosition(List) - start;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            (_dragStart, _dragPress) = (null, null);
            var paths = Sel.Select(i => i.FullPath).ToList();
            if (paths.Count == 0) return;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, string.Join('\n', paths)));
            // Real file items too, so other apps (Nautilus, a browser upload, an editor) accept the drop.
            foreach (var p in paths)
            {
                IStorageItem? si = Directory.Exists(p) ? await StorageProvider.TryGetFolderFromPathAsync(p) : await StorageProvider.TryGetFileFromPathAsync(p);
                if (si != null) data.Add(DataTransferItem.CreateFile(si));
            }
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);
        }, handledEventsToo: true);
        AcceptDrops(List, e => ItemAt(e.Source) is { IsDirectory: true } it ? it.FullPath : _cur is { SearchQuery: null } t ? t.Path : null);
    }

    void AcceptDrops(Control c, Func<DragEventArgs, string?> target)
    {
        DragDrop.SetAllowDrop(c, true);
        c.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var dest = target(e);
            var paths = DroppedPaths(e);
            e.DragEffects = dest != null && paths.Count > 0 ? (MoveOnDrop(e, paths, dest) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
            e.Handled = true;
        });
        c.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            if (target(e) is { } dest) DropInto(DroppedPaths(e), dest, MoveOnDrop(e, DroppedPaths(e), dest));
        });
    }

    static List<string> DroppedPaths(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(DragFormat) is { } s) return s.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        return e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? new();
    }

    // Like Explorer: Shift moves, Ctrl copies, otherwise move on the same drive and copy across drives.
    // Never move when the source only offers a copy (most other apps do), so their file stays put.
    static bool MoveOnDrop(DragEventArgs e, List<string> paths, string dest) =>
        e.DragEffects.HasFlag(DragDropEffects.Move) &&
        (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && paths.All(p => Platform.SameDrive(p, dest))));

    internal void DropInto(List<string> paths, string dest, bool move)
    {
        // Never drop a folder into itself or its own subfolder; moving into the folder it is already in does nothing.
        var ok = paths.Where(p => !Platform.Same(p, dest) && !dest.StartsWith(p.TrimEnd('/') + "/") && !(move && Platform.Same(Path.GetDirectoryName(p), dest))).ToList();
        if (ok.Count < paths.Count) Notify("Skipped items that are already there or would go into themselves.");
        if (ok.Count > 0) _ = TransferAsync(ok, dest, move);
    }
}
