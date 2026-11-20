using System.Diagnostics;
using System.Threading.Channels;
using Avalonia.Threading;
using Frost.Core;
namespace Frost.Linux;

/// <summary>One tab: history, folder listing, filter/search and the folder watcher (port of TabViewModel).</summary>
public sealed class Tab : IDisposable
{
    readonly AppSettings _settings;
    readonly Action<string> _notify;
    readonly NavigationHistory _history;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    FileSystemWatcher? _watcher;
    CancellationTokenSource? _listCts, _searchCts;
    List<FileEntry> _folder = new(), _found = new();
    int _rebuildVer;
    bool _disposed;
    string _filter = "";
    string? _searchQuery;
    List<string>? _pendingSelect;

    public Tab(AppSettings settings, Action<string> notify, string path)
    {
        _settings = settings; _notify = notify;
        _history = new NavigationHistory(path);
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (!_disposed && _searchQuery == null && Loaded) _ = LoadAsync(quiet: true); };
    }

    public event Action<Tab>? Rebuilt, Navigated, StatusChanged;
    public bool Loaded { get; private set; }
    public List<Item> Items { get; private set; } = new();
    public IReadOnlyList<Item> Selection { get; set; } = Array.Empty<Item>();
    public List<string>? TakePendingSelect() { var p = _pendingSelect; _pendingSelect = null; return p; }
    public string Path => _history.Current;
    public string Title => System.IO.Path.GetFileName(Path.TrimEnd('/')) is { Length: > 0 } n ? n : Path;
    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;
    public string? SearchQuery => _searchQuery;
    public bool IsSearching { get; private set; }
    public bool IsLoading { get; private set; }
    public string CountText { get; private set; } = "";
    public string FreeText { get; private set; } = "";
    public string Overlay { get; private set; } = "";

    public void SetFilter(string text)
    {
        if (_filter == text) return;
        _filter = text; CancelSearch(); _ = RebuildAsync();
    }

    // ---- navigation ----
    public void EnsureLoaded() { if (!Loaded) { Loaded = true; _ = LoadAsync(); } }

    public void Navigate(string path)
    {
        if (Platform.Same(path, Path)) { Refresh(); return; }
        if (!Directory.Exists(path)) { _notify($"Can't find \"{path}\"."); return; }
        _history.Navigate(path); AfterNav(null);
    }
    public void GoBack() { if (_history.CanGoBack) { _history.Back(); AfterNav(null); } }
    public void GoForward() { if (_history.CanGoForward) { _history.Forward(); AfterNav(null); } }
    public void Up() { var old = Path; if (_history.Parent() is { } p) { _history.Navigate(p); AfterNav(old); } }
    public void Refresh() => _ = LoadAsync();
    public Task RefreshAsync(string? select = null) => LoadAsync(select, quiet: true);

    void AfterNav(string? select)
    {
        Loaded = true; CancelSearch(); _filter = "";
        Navigated?.Invoke(this);
        _ = LoadAsync(select);
    }

    // ---- listing ----
    public async Task LoadAsync(string? select = null, bool quiet = false)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        if (select != null) _pendingSelect = new() { select };
        else if (quiet && Selection.Count > 0) _pendingSelect = Selection.Select(s => s.FullPath).ToList();
        if (!quiet) { IsLoading = true; StatusChanged?.Invoke(this); }
        var path = Path;
        try
        {
            var list = await DirectoryLister.ListAsync(path, _settings.ShowHidden, cts.Token);
            if (cts.IsCancellationRequested) return;
            _folder = list.ToList();
            if (_searchQuery == null) await RebuildAsync();
            SetupWatcher(path);
            _ = UpdateFreeAsync(path);
        }
        catch (OperationCanceledException) { return; }
        catch (UnauthorizedAccessException) { Fail("You don't have permission to open this folder."); }
        catch (DirectoryNotFoundException) { Fail("This folder doesn't exist any more."); }
        catch (Exception ex) { Fail(ex.Message); }
        finally { if (_listCts == cts) { IsLoading = false; StatusChanged?.Invoke(this); } }
    }

    void Fail(string text)
    {
        _folder = new(); Items = new(); Overlay = text; CountText = "";
        Rebuilt?.Invoke(this);
    }

    async Task UpdateFreeAsync(string path)
    {
        FreeText = await Task.Run(() => { try { return Format.Size(new DriveInfo(path).AvailableFreeSpace) + " free"; } catch { return ""; } });
        StatusChanged?.Invoke(this);
    }

    async Task RebuildAsync()
    {
        var ver = ++_rebuildVer;
        bool searching = _searchQuery != null;
        var entries = searching ? _found.ToList() : _folder;
        var filter = searching ? "" : _filter.Trim();
        var (col, desc) = (_settings.SortColumn, _settings.SortDescending);
        IReadOnlyList<FileEntry> sorted;
        try
        {
            sorted = await Task.Run(() =>
            {
                var src = filter.Length > 0 ? entries.Where(e => Search.Matches(e.Name, filter)) : entries;
                return Sorter.Sort(src, col, desc);
            });
        }
        catch (Exception ex) { Fail(ex.Message); return; }
        if (ver != _rebuildVer || _disposed) return;
        var now = DateTime.Now;
        var items = sorted.Select(e => new Item(e, now)).ToList(); // glyphs are Avalonia objects: build on the UI thread
        Items = items;
        CountText = items.Count == 1 ? "1 item" : $"{items.Count} items";
        Overlay = items.Count > 0 ? "" : searching ? (IsSearching ? "" : "No results found") : filter.Length > 0 ? "No items match your filter" : "This folder is empty";
        Rebuilt?.Invoke(this);
    }

    public void Resort() { if (Loaded) _ = RebuildAsync(); }

    // ---- recursive search (Enter in the search box) ----
    public async void StartSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        _searchQuery = query; _found = new(); IsSearching = true;
        StatusChanged?.Invoke(this);
        _ = RebuildAsync();
        var ch = Channel.CreateUnbounded<FileEntry>();
        var (root, hidden) = (Path, _settings.ShowHidden);
        _ = Task.Run(async () =>
        {
            try { await foreach (var e in Search.RunAsync(root, query, hidden, ct).WithCancellation(ct)) ch.Writer.TryWrite(e); ch.Writer.TryComplete(); }
            catch (Exception ex) { ch.Writer.TryComplete(ex); }
        });
        try
        {
            var sw = Stopwatch.StartNew(); var dirty = false;
            while (await ch.Reader.WaitToReadAsync(ct))
            {
                while (ch.Reader.TryRead(out var e)) { _found.Add(e); dirty = true; }
                if (dirty && sw.ElapsedMilliseconds > 250) { dirty = false; sw.Restart(); _ = RebuildAsync(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _notify("Search failed: " + ex.Message); }
        if (_searchCts == cts)
        {
            IsSearching = false;
            await RebuildAsync();
            CountText = Items.Count == 1 ? "1 result" : Items.Count + " results";
            StatusChanged?.Invoke(this);
        }
    }

    public void CancelSearch()
    {
        _searchCts?.Cancel(); _searchCts = null;
        bool had = _searchQuery != null;
        _searchQuery = null; IsSearching = false;
        if (had) { _ = RebuildAsync(); StatusChanged?.Invoke(this); }
    }

    // ---- watcher: reload 300 ms after the last change ----
    void SetupWatcher(string path)
    {
        DisposeWatcher();
        if (_disposed) return;
        try
        {
            var w = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Poke(object? s, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_disposed) { _debounce.Stop(); _debounce.Start(); } });
            w.Created += Poke; w.Deleted += Poke; w.Renamed += Poke; w.Changed += Poke; w.Error += Poke;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { } // e.g. inotify limit reached: F5 still works
    }

    void DisposeWatcher() { try { _watcher?.Dispose(); } catch { } _watcher = null; }

    public void Dispose()
    {
        _disposed = true; _debounce.Stop();
        _listCts?.Cancel(); _searchCts?.Cancel();
        DisposeWatcher();
    }
}
