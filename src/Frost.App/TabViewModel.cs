using System.Collections;
using System.Diagnostics;
using System.Threading.Channels;
using System.Windows.Threading;
using Frost.Core;

namespace Frost.App;

public sealed record Crumb(string Name, string Path);

public sealed class TabViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly NavigationHistory _history;
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _listCts, _searchCts, _thumbCts = new();
    private List<FileEntry> _folderEntries = new();
    private List<FileEntry> _searchEntries = new();
    private int _rebuildVer;
    private bool _disposed, _active, _loading, _isSearching;
    private string _searchText = "";
    private string? _searchQuery;
    private string? _error;
    private string _countText = "", _selectionText = "", _freeText = "";
    private bool _showOverlay;
    private string _overlayGlyph = "", _overlayText = "";
    private IReadOnlyList<Crumb> _crumbs = Array.Empty<Crumb>();
    private List<string>? _pendingSelect;

    public TabViewModel(MainViewModel main, string path)
    {
        _main = main;
        _history = new NavigationHistory(path);
        _debounce = new Timer(_ => _ui.BeginInvoke(new Action(OnDebounce)));
    }

    public bool Loaded { get; private set; }
    public BulkCollection<ItemViewModel> Items { get; } = new();
    public IReadOnlyList<ItemViewModel> Selection { get; private set; } = Array.Empty<ItemViewModel>();
    public List<string>? PendingSelect => _pendingSelect;
    public void ClearPendingSelect() => _pendingSelect = null;

    public string Path => _history.Current;
    public string Title
    {
        get
        {
            var p = Path.TrimEnd('\\', '/');
            if (p.Length == 0 || p.EndsWith(':')) return Path;
            var n = System.IO.Path.GetFileName(p);
            return n.Length == 0 ? Path : n;
        }
    }
    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;
    public IReadOnlyList<Crumb> Crumbs => _crumbs;

    public bool IsActive { get => _active; set => Set(ref _active, value); }
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) Raise(nameof(StatusBusy)); } }
    public bool IsSearching { get => _isSearching; private set { if (Set(ref _isSearching, value)) Raise(nameof(StatusBusy)); } }
    public string StatusBusy => IsSearching ? "Searching…" : IsLoading ? "Loading…" : "";
    public string? SearchQuery => _searchQuery;
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }
    public string FreeText { get => _freeText; private set => Set(ref _freeText, value); }
    public bool ShowOverlay { get => _showOverlay; private set => Set(ref _showOverlay, value); }
    public string OverlayGlyph { get => _overlayGlyph; private set => Set(ref _overlayGlyph, value); }
    public string OverlayText { get => _overlayText; private set => Set(ref _overlayText, value); }
    public bool HasSelection => Selection.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Raise();
            CancelSearch();
            _ = RebuildAsync();
        }
    }

    // ---- navigation ----
    public void EnsureLoaded()
    {
        if (Loaded) return;
        Loaded = true;
        RaiseNav();
        _ = LoadAsync();
    }

    public void Navigate(string path)
    {
        if (Paths.Same(path, Path)) { Refresh(); return; }
        if (!Directory.Exists(path)) { _main.Notify($"Can't find \"{path}\"."); return; }
        var old = Path;
        _history.Navigate(path);
        AfterNav(null);
    }

    public void GoBack() { if (!_history.CanGoBack) return; _history.Back(); AfterNav(null); }
    public void GoForward() { if (!_history.CanGoForward) return; _history.Forward(); AfterNav(null); }
    public void Up()
    {
        var p = _history.Parent();
        if (p == null) return;
        var old = Path;
        _history.Navigate(p);
        AfterNav(old);
    }
    public void Refresh() { _ = LoadAsync(); }
    public Task RefreshAsync(string? select = null) => LoadAsync(select, quiet: true);

    private void AfterNav(string? select)
    {
        Loaded = true;
        CancelSearch();
        if (_searchText.Length > 0) { _searchText = ""; Raise(nameof(SearchText)); }
        RaiseNav();
        _ = LoadAsync(select);
        _main.OnTabNavigated(this);
    }

    private void RaiseNav()
    {
        _crumbs = BuildCrumbs();
        Raise(nameof(Path)); Raise(nameof(Title)); Raise(nameof(Crumbs));
        Raise(nameof(CanGoBack)); Raise(nameof(CanGoForward));
    }

    private IReadOnlyList<Crumb> BuildCrumbs()
    {
        try { return PathUtil.Segments(Path).Select(s => new Crumb(s.Name, s.Path)).ToList(); }
        catch { return new[] { new Crumb(Path, Path) }; }
    }

    // ---- listing ----
    public async Task LoadAsync(string? select = null, bool quiet = false)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        var oldThumb = _thumbCts; _thumbCts = new CancellationTokenSource(); oldThumb?.Cancel();
        if (select != null) _pendingSelect = new List<string> { select };
        else if (quiet && Selection.Count > 0) _pendingSelect = Selection.Select(s => s.FullPath).ToList();
        if (!quiet) IsLoading = true;
        _error = null;
        var path = Path;
        try
        {
            var list = await DirectoryLister.ListAsync(path, _main.Settings.ShowHidden, cts.Token);
            if (cts.IsCancellationRequested) return;
            _folderEntries = list.ToList();
            if (_searchQuery == null) await RebuildAsync();
            SetupWatcher(path);
            _ = UpdateFreeSpaceAsync(path);
        }
        catch (OperationCanceledException) { return; }
        catch (UnauthorizedAccessException) { Fail("You don't have permission to open this folder.", ""); }
        catch (DirectoryNotFoundException) { Fail("This folder doesn't exist any more.", ""); }
        catch (Exception ex) { Fail(ex.Message, ""); }
        finally { if (_listCts == cts) IsLoading = false; }
    }

    private void Fail(string text, string glyph)
    {
        _error = text;
        _folderEntries = new List<FileEntry>();
        Items.ReplaceAll(Array.Empty<ItemViewModel>());
        OverlayText = text; OverlayGlyph = glyph; ShowOverlay = true;
        CountText = "";
    }

    private async Task UpdateFreeSpaceAsync(string path)
    {
        try
        {
            var text = await Task.Run(() =>
            {
                var root = System.IO.Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return "";
                return Format.Size(new DriveInfo(root).AvailableFreeSpace) + " free";
            });
            FreeText = text;
        }
        catch { FreeText = ""; }
    }

    private async Task RebuildAsync()
    {
        var ver = ++_rebuildVer;
        var searching = _searchQuery != null;
        var entries = searching ? _searchEntries.ToList() : _folderEntries;
        var filter = searching ? "" : _searchText.Trim();
        var col = _main.Settings.SortColumn;
        var desc = _main.Settings.SortDescending;
        var ct = _thumbCts?.Token ?? CancellationToken.None;
        List<ItemViewModel> vms;
        try
        {
            vms = await Task.Run(() =>
            {
                var now = DateTime.Now;
                IEnumerable<FileEntry> src = entries;
                if (filter.Length > 0) src = src.Where(e => Search.Matches(e.Name, filter));
                return Sorter.Sort(src, col, desc).Select(e => new ItemViewModel(e, now, ct)).ToList();
            });
        }
        catch (Exception ex) { Fail(ex.Message, ""); return; }
        if (ver != _rebuildVer || _disposed) return;
        Items.ReplaceAll(vms);
        CountText = vms.Count == 1 ? "1 item" : $"{vms.Count} items";
        if (_error != null && !searching) { _error = null; }
        if (vms.Count == 0)
        {
            ShowOverlay = true;
            OverlayGlyph = searching ? "" : "";
            OverlayText = searching ? (IsSearching ? "" : "No results found") : filter.Length > 0 ? "No items match your filter" : "This folder is empty";
        }
        else ShowOverlay = false;
        _main.RaiseItemsRebuilt(this);
    }

    public void Resort() { if (Loaded) _ = RebuildAsync(); }
    public void ClearFilterAndRefocus() => SearchText = "";

    public void BeginRename(string path) => Items.FirstOrDefault(i => Paths.Same(i.FullPath, path))?.BeginRename();

    // ---- selection ----
    public void SetSelection(IList items)
    {
        Selection = items.Cast<ItemViewModel>().ToList();
        long bytes = Selection.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size);
        SelectionText = Selection.Count == 0 ? "" : Selection.Count + " selected" + (bytes > 0 ? $" ({Format.Size(bytes)})" : "");
        Raise(nameof(Selection)); Raise(nameof(HasSelection));
    }

    // ---- search ----
    public async void StartSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        _searchQuery = query;
        _searchEntries = new List<FileEntry>();
        IsSearching = true;
        Raise(nameof(SearchQuery));
        _ = RebuildAsync();
        var ch = Channel.CreateUnbounded<FileEntry>();
        var root = Path;
        bool hidden = _main.Settings.ShowHidden;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in Search.RunAsync(root, query, hidden, ct).WithCancellation(ct))
                    ch.Writer.TryWrite(e);
                ch.Writer.TryComplete();
            }
            catch (Exception ex) { ch.Writer.TryComplete(ex); }
        });
        try
        {
            var sw = Stopwatch.StartNew();
            var dirty = false;
            while (await ch.Reader.WaitToReadAsync(ct))
            {
                while (ch.Reader.TryRead(out var e)) { _searchEntries.Add(e); dirty = true; }
                if (dirty && sw.ElapsedMilliseconds > 250) { dirty = false; sw.Restart(); _ = RebuildAsync(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _main.Notify("Search failed: " + ex.Message); }
        if (_searchCts == cts)
        {
            IsSearching = false;
            await RebuildAsync();
            CountText = Items.Count == 1 ? "1 result" : Items.Count + " results";
        }
    }

    public void CancelSearch()
    {
        var had = _searchQuery != null;
        _searchCts?.Cancel();
        _searchCts = null;
        _searchQuery = null;
        IsSearching = false;
        if (had) { Raise(nameof(SearchQuery)); }
    }

    // ---- watcher ----
    private void SetupWatcher(string path)
    {
        DisposeWatcher();
        if (_disposed) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                IncludeSubdirectories = false
            };
            w.Created += (_, _) => Poke();
            w.Deleted += (_, _) => Poke();
            w.Renamed += (_, _) => Poke();
            w.Changed += (_, _) => Poke();
            w.Error += (_, _) => Poke();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { }
    }

    private void Poke() { if (!_disposed) try { _debounce.Change(300, Timeout.Infinite); } catch { } }

    private void OnDebounce()
    {
        if (_disposed || _searchQuery != null || !Loaded) return;
        if (Items.Any(i => i.IsRenaming)) { Poke(); return; }
        _ = LoadAsync(null, quiet: true);
    }

    private void DisposeWatcher() { try { _watcher?.Dispose(); } catch { } _watcher = null; }

    public void Dispose()
    {
        _disposed = true;
        _listCts?.Cancel(); _searchCts?.Cancel(); _thumbCts?.Cancel();
        DisposeWatcher();
        _debounce.Dispose();
    }

    public TabViewModel Duplicate() => new(_main, Path);
}
