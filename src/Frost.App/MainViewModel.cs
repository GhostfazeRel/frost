using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly Stack<string> _closed = new();
    private DispatcherTimer? _noticeTimer;
    private TabViewModel? _current;
    private string _notice = "";
    private double _tabWidth = 180;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Sections = new List<SidebarSection>
        {
            new("Quick access"),
            new("Pinned", "Right-click a folder and choose \"Pin to sidebar\""),
            new("Drives"),
        };
        foreach (var s in Sections) s.IsExpanded = !settings.CollapsedSections.Contains(s.Title);
        CloseTabCommand = new RelayCommand(o => CloseTab(o as TabViewModel));
        CancelSearchCommand = new RelayCommand(_ => CurrentTab?.CancelSearch());
    }

    public AppSettings Settings { get; }
    public Window? Owner { get; set; }
    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<OperationViewModel> Operations { get; } = new();
    public List<SidebarSection> Sections { get; }
    public PreviewViewModel Preview { get; } = new();
    public ICommand CloseTabCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public event Action<TabViewModel>? ItemsRebuilt;

    public TabViewModel? CurrentTab { get => _current; private set => Set(ref _current, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public double TabWidth { get => _tabWidth; set => Set(ref _tabWidth, value); }

    // ---- view settings ----
    public ViewMode ViewMode => Settings.View;
    public bool IsDetails => Settings.View == ViewMode.Details;
    public bool IsIcons => !IsDetails;
    public double IconSize => Settings.IconSize;
    public double TileWidth => Settings.IconSize + 36;
    public double TileHeight => Settings.IconSize + 52;

    public void SetView(ViewMode m)
    {
        Settings.View = m;
        if (m != ViewMode.Details) Settings.IconSize = m == ViewMode.Tiles ? 64 : m == ViewMode.Large ? 112 : 160;
        RaiseView();
    }

    public void ChangeIconSize(double delta)
    {
        if (IsDetails) return;
        Settings.IconSize = Math.Clamp(Settings.IconSize + delta, 40, 192);
        RaiseView();
    }

    private void RaiseView()
    {
        Raise(nameof(ViewMode)); Raise(nameof(IsDetails)); Raise(nameof(IsIcons));
        Raise(nameof(IconSize)); Raise(nameof(TileWidth)); Raise(nameof(TileHeight));
    }

    public bool ShowHidden
    {
        get => Settings.ShowHidden;
        set { if (Settings.ShowHidden == value) return; Settings.ShowHidden = value; Raise(); foreach (var t in Tabs) if (t.Loaded) t.Refresh(); }
    }

    public bool PreviewPane
    {
        get => Settings.PreviewPane;
        set { if (Settings.PreviewPane == value) return; Settings.PreviewPane = value; Raise(); if (value) UpdatePreview(); }
    }

    public void SetSort(SortColumn col)
    {
        if (Settings.SortColumn == col) Settings.SortDescending = !Settings.SortDescending;
        else { Settings.SortColumn = col; Settings.SortDescending = false; }
        RaiseSort();
    }

    public void SetSortDirection(bool descending) { Settings.SortDescending = descending; RaiseSort(); }

    private void RaiseSort()
    {
        Raise("Sort");
        foreach (var t in Tabs) t.Resort();
    }

    // ---- startup / shutdown ----
    public void Start(string? startPath)
    {
        foreach (var p in Settings.OpenTabs)
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) AddTab(new TabViewModel(this, p));
        if (startPath != null && Directory.Exists(startPath)) { AddTab(new TabViewModel(this, startPath)); Activate(Tabs[^1]); }
        else if (Tabs.Count == 0) { AddTab(new TabViewModel(this, Paths.Home)); Activate(Tabs[0]); }
        else Activate(Tabs[Math.Clamp(Settings.ActiveTab, 0, Tabs.Count - 1)]);
        _ui.BeginInvoke(DispatcherPriority.Background, new Action(LoadSidebar));
    }

    public void SaveState()
    {
        Settings.OpenTabs = Tabs.Select(t => t.Path).ToList();
        Settings.ActiveTab = CurrentTab == null ? 0 : Tabs.IndexOf(CurrentTab);
        Settings.CollapsedSections = Sections.Where(s => !s.IsExpanded).Select(s => s.Title).ToList();
        SettingsStore.Save(Settings);
    }

    // ---- tabs ----
    private void AddTab(TabViewModel t, int? index = null)
    {
        if (index is int i && i <= Tabs.Count) Tabs.Insert(i, t); else Tabs.Add(t);
    }

    public void NewTab(string? path = null, bool activate = true)
    {
        var p = path != null && Directory.Exists(path) ? path : Paths.Home;
        var t = new TabViewModel(this, p);
        AddTab(t);
        if (activate) Activate(t);
    }

    public void OpenInNewTab(string path) => NewTab(path, activate: false);

    public void DuplicateTab(TabViewModel? t)
    {
        if (t == null) return;
        var d = t.Duplicate();
        AddTab(d, Tabs.IndexOf(t) + 1);
        Activate(d);
    }

    public void Activate(TabViewModel t)
    {
        if (CurrentTab == t) return;
        if (CurrentTab != null) CurrentTab.IsActive = false;
        CurrentTab = t;
        t.IsActive = true;
        t.EnsureLoaded();
        UpdateSidebarCurrent();
        UpdatePreview();
    }

    public void CloseTab(TabViewModel? t)
    {
        if (t == null) return;
        if (Tabs.Count <= 1) { Owner?.Close(); return; }
        var i = Tabs.IndexOf(t);
        _closed.Push(t.Path);
        Tabs.Remove(t);
        t.Dispose();
        if (CurrentTab == t) { CurrentTab = null; Activate(Tabs[Math.Min(i, Tabs.Count - 1)]); }
    }

    public void ReopenClosedTab() { if (_closed.Count > 0) NewTab(_closed.Pop()); }

    public void CycleTab(int dir)
    {
        if (CurrentTab == null || Tabs.Count < 2) return;
        Activate(Tabs[(Tabs.IndexOf(CurrentTab) + dir + Tabs.Count) % Tabs.Count]);
    }

    public void OnTabNavigated(TabViewModel t) { if (t == CurrentTab) { UpdateSidebarCurrent(); UpdatePreview(); } }
    public void RaiseItemsRebuilt(TabViewModel t) { ItemsRebuilt?.Invoke(t); if (t == CurrentTab) UpdatePreview(); }

    // ---- sidebar ----
    private void LoadSidebar()
    {
        try
        {
            var q = Sections[0].Items;
            q.Clear();
            foreach (var f in Places.QuickAccess()) q.Add(new SidebarItem(f.Name, f.Path, f.Glyph));
        }
        catch
        {
            Sections[0].Items.Clear();
            Sections[0].Items.Add(new SidebarItem("Home", Paths.Home, ""));
        }
        Sections[1].Items.Clear();
        foreach (var p in Settings.Pinned) Sections[1].Items.Add(new SidebarItem(System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p, p, "") { IsPinned = true });
        LoadDrives();
        UpdateSidebarCurrent();
    }

    public void LoadDrives()
    {
        try
        {
            var d = Sections[2].Items;
            d.Clear();
            foreach (var x in Places.Drives())
            {
                var name = string.IsNullOrWhiteSpace(x.Label) ? "Local Disk" : x.Label;
                var used = x.TotalBytes > 0 ? 100.0 * (x.TotalBytes - x.FreeBytes) / x.TotalBytes : 0;
                d.Add(new SidebarItem($"{name} ({x.Name.TrimEnd('\\')})", x.RootPath, x.Kind == DriveKind.Removable ? "" : x.Kind == DriveKind.Network ? "" : "")
                {
                    IsDrive = true, UsedPercent = used,
                    FreeText = $"{Format.Size(x.FreeBytes)} free of {Format.Size(x.TotalBytes)}"
                });
            }
        }
        catch { }
    }

    private void UpdateSidebarCurrent()
    {
        var p = CurrentTab?.Path;
        foreach (var s in Sections) foreach (var i in s.Items) i.IsCurrent = Paths.Same(i.Path, p);
    }

    public void TogglePin(string path)
    {
        var existing = Settings.Pinned.FirstOrDefault(x => Paths.Same(x, path));
        if (existing != null)
        {
            Settings.Pinned.Remove(existing);
            var item = Sections[1].Items.FirstOrDefault(i => Paths.Same(i.Path, path));
            if (item != null) Sections[1].Items.Remove(item);
        }
        else
        {
            Settings.Pinned.Add(path);
            var n = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            Sections[1].Items.Add(new SidebarItem(n.Length > 0 ? n : path, path, "") { IsPinned = true });
            UpdateSidebarCurrent();
        }
        SettingsStore.Save(Settings);
    }

    public bool IsPinned(string path) => Settings.Pinned.Any(x => Paths.Same(x, path));

    // ---- notices ----
    public void Notify(string text)
    {
        Notice = text;
        _noticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick -= ClearNotice;
        _noticeTimer.Tick += ClearNotice;
        _noticeTimer.Stop(); _noticeTimer.Start();
    }

    private void ClearNotice(object? s, EventArgs e) { Notice = ""; _noticeTimer?.Stop(); }

    // ---- preview ----
    private DispatcherTimer? _previewTimer;
    public void UpdatePreview()
    {
        if (!PreviewPane) return;
        _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewTimer.Tick -= PreviewTick;
        _previewTimer.Tick += PreviewTick;
        _previewTimer.Stop(); _previewTimer.Start();
    }

    private void PreviewTick(object? s, EventArgs e)
    {
        _previewTimer?.Stop();
        Preview.Show(CurrentTab?.Selection ?? Array.Empty<ItemViewModel>(), CurrentTab);
    }

    // ---- file actions ----
    private IReadOnlyList<string> SelectedPaths => CurrentTab?.Selection.Select(i => i.FullPath).ToList() ?? new List<string>();

    public void Open(ItemViewModel it, bool newTab = false)
    {
        if (it.IsDirectory) { if (newTab) OpenInNewTab(it.FullPath); else CurrentTab?.Navigate(it.FullPath); return; }
        try { ShellActions.Open(it.FullPath); } catch (Exception ex) { Notify("Couldn't open file: " + ex.Message); }
    }

    public void OpenSelection()
    {
        var sel = CurrentTab?.Selection;
        if (sel == null || sel.Count == 0) return;
        if (sel.Count == 1) { Open(sel[0]); return; }
        foreach (var i in sel) Open(i, newTab: true);
    }

    public void CopySelection(bool cut)
    {
        var p = SelectedPaths;
        if (p.Count == 0) return;
        try { FileClipboard.SetFiles(p, cut); Notify(cut ? $"Cut {p.Count} item(s)" : $"Copied {p.Count} item(s)"); }
        catch (Exception ex) { Notify("Clipboard error: " + ex.Message); }
    }

    public void CopyPath()
    {
        var p = SelectedPaths;
        var text = p.Count > 0 ? string.Join("\r\n", p.Select(x => "\"" + x + "\"")) : CurrentTab?.Path;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Notify("Path copied"); } catch { }
    }

    public bool CanPaste() { try { return FileClipboard.HasFiles(); } catch { return false; } }

    public void Paste(string? dest = null)
    {
        dest ??= CurrentTab?.Path;
        if (dest == null) return;
        IReadOnlyList<string> files; bool cut;
        try { files = FileClipboard.GetFiles(out cut); } catch (Exception ex) { Notify("Clipboard error: " + ex.Message); return; }
        if (files.Count == 0) return;
        _ = TransferAsync(files, dest, cut);
        if (cut) try { Clipboard.Clear(); } catch { }
    }

    public async void Delete(bool permanent)
    {
        var t = CurrentTab;
        var paths = SelectedPaths;
        if (t == null || paths.Count == 0 || Owner == null) return;
        try
        {
            if (permanent)
            {
                var msg = paths.Count == 1 ? $"Permanently delete \"{System.IO.Path.GetFileName(paths[0])}\"? This can't be undone."
                                           : $"Permanently delete these {paths.Count} items? This can't be undone.";
                if (DialogWindow.Show(Owner, "Delete permanently", msg, new[] { "Delete", "Cancel" }, null, out _) != 0) return;
                await Task.Run(() => FileOperations.DeletePermanently(paths));
            }
            else ShellActions.Recycle(paths, Owner);
        }
        catch (Exception ex) { Notify("Delete failed: " + ex.Message); }
        _ = t.RefreshAsync();
    }

    public async void NewFolder()
    {
        var t = CurrentTab;
        if (t == null || t.SearchQuery != null) return;
        try { var p = FileOperations.CreateFolder(t.Path); await t.RefreshAsync(p); t.BeginRename(p); }
        catch (Exception ex) { Notify("Couldn't create folder: " + ex.Message); }
    }

    public async void NewTextFile()
    {
        var t = CurrentTab;
        if (t == null || t.SearchQuery != null) return;
        try { var p = FileOperations.CreateFile(t.Path, "New Text Document.txt"); await t.RefreshAsync(p); t.BeginRename(p); }
        catch (Exception ex) { Notify("Couldn't create file: " + ex.Message); }
    }

    public void BeginRenameSelection()
    {
        var sel = CurrentTab?.Selection;
        if (sel is { Count: 1 }) sel[0].BeginRename();
    }

    /// <summary>Returns false when the name was invalid and the editor should stay open.</summary>
    public async Task<bool> CommitRename(ItemViewModel it, bool lostFocus)
    {
        var t = CurrentTab;
        var name = it.EditName.Trim();
        if (name == it.Name || name.Length == 0) { it.IsRenaming = false; return true; }
        if (!PathUtil.IsValidFileName(name))
        {
            if (lostFocus) { it.IsRenaming = false; return true; }
            Notify("A file name can't be empty, end with a dot or contain  \\ / : * ? \" < > |");
            return false;
        }
        it.IsRenaming = false;
        try { var np = FileOperations.Rename(it.FullPath, name); if (t != null) await t.RefreshAsync(np); }
        catch (Exception ex) { Notify("Rename failed: " + ex.Message); }
        return true;
    }

    public void Properties(string? path = null)
    {
        path ??= CurrentTab?.Selection.Count > 0 ? CurrentTab.Selection[0].FullPath : CurrentTab?.Path;
        if (path == null) return;
        try { ShellActions.ShowProperties(path); } catch (Exception ex) { Notify(ex.Message); }
    }

    public void OpenTerminal(string? folder = null)
    {
        folder ??= CurrentTab?.Path;
        if (folder == null) return;
        try { ShellActions.OpenTerminal(folder); } catch (Exception ex) { Notify(ex.Message); }
    }

    public void OpenWith(string path) { try { ShellActions.OpenWith(path); } catch (Exception ex) { Notify(ex.Message); } }

    // ---- transfers ----
    public async Task TransferAsync(IReadOnlyList<string> sources, string dest, bool move)
    {
        var op = new OperationViewModel(move ? "Moving" : "Copying");
        Operations.Add(op);
        var progress = new Progress<OperationProgress>(op.Report);
        ConflictChoice? all = null;
        ConflictChoice OnConflict(string target)
        {
            if (all != null) return all.Value;
            return _ui.Invoke(() =>
            {
                var idx = DialogWindow.Show(Owner, "File already exists",
                    $"\"{System.IO.Path.GetFileName(target)}\" already exists in the destination folder.",
                    new[] { "Replace", "Skip", "Keep both" }, "Do this for all conflicts", out var applyAll);
                var c = idx switch { 0 => ConflictChoice.Replace, 2 => ConflictChoice.KeepBoth, _ => ConflictChoice.Skip };
                if (applyAll) all = c;
                return c;
            });
        }
        try { await FileOperations.TransferAsync(sources, dest, move, OnConflict, progress, op.Cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify((move ? "Move" : "Copy") + " failed: " + ex.Message); }
        finally
        {
            Operations.Remove(op);
            foreach (var t in Tabs)
                if (t.Loaded && (Paths.Same(t.Path, dest) || sources.Any(s => Paths.Same(System.IO.Path.GetDirectoryName(s), t.Path)))) _ = t.RefreshAsync();
        }
    }
}
