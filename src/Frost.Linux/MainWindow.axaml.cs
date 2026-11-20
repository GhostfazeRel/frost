using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Frost.Core;
namespace Frost.Linux;

public partial class MainWindow : Window
{
    readonly AppSettings _s = Platform.LoadSettings();
    readonly List<Tab> _tabs = new();
    readonly Stack<string> _closed = new();
    readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    Tab? _cur;
    CancellationTokenSource? _previewCts;
    bool _ctrlDown;
    readonly Button[] _headers = new Button[4];
    static readonly string[] ColNames = { "Name", "Date modified", "Type", "Size" };
    static readonly SortColumn[] ColSorts = { SortColumn.Name, SortColumn.Modified, SortColumn.Type, SortColumn.Size };

    public MainWindow() : this(null) { }

    public MainWindow(string? startPath)
    {
        InitializeComponent();
        if (_s.WindowWidth >= MinWidth && _s.WindowHeight >= MinHeight) { Width = _s.WindowWidth; Height = _s.WindowHeight; }
        if (_s.Maximized) WindowState = WindowState.Maximized;
        // Glass when the compositor can blur; plain see-through when it only composites alpha (niri/XWayland); else opaque.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None };
        // The level can already be final before this handler is attached, so apply it once now too.
        void ApplyBackdrop() => Root.Background = Dialogs.Res(ActualTransparencyLevel == WindowTransparencyLevel.None ? "WindowFallback"
            : ActualTransparencyLevel == WindowTransparencyLevel.Transparent ? "WindowClear" : "WindowTint");
        ApplyBackdrop();
        PropertyChanged += (_, e) => { if (e.Property == ActualTransparencyLevelProperty) ApplyBackdrop(); };

        NewTabButton.Click += (_, _) => NewTab();
        BackButton.Click += (_, _) => _cur?.GoBack();
        ForwardButton.Click += (_, _) => _cur?.GoForward();
        UpButton.Click += (_, _) => _cur?.Up();
        RefreshButton.Click += (_, _) => Refresh();
        NewButton.Click += (_, _) => Flyout(NewButton, NewMenuItems());
        CutButton.Click += (_, _) => CopySelection(true);
        CopyButton.Click += (_, _) => CopySelection(false);
        PasteButton.Click += (_, _) => Paste();
        RenameButton.Click += (_, _) => RenameSelection();
        DeleteButton.Click += (_, _) => DeleteSelection(false);
        SortButton.Click += (_, _) => Flyout(SortButton, SortMenuItems());
        ViewButton.Click += (_, _) => Flyout(ViewButton, ViewMenuItems());
        MoreButton.Click += (_, _) => Flyout(MoreButton, MoreMenuItems());
        CrumbScroll.PointerPressed += (_, e) => { if (e.Source is not Button && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginAddressEdit(); };
        AddressBox.KeyDown += AddressKey;
        AddressBox.LostFocus += (_, _) => EndAddressEdit();
        SearchBox.TextChanged += (_, _) => _cur?.SetFilter(SearchBox.Text ?? "");
        SearchBox.KeyDown += SearchKey;
        _noticeTimer.Tick += (_, _) => { NoticeText.Text = ""; _noticeTimer.Stop(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = ShowPreview(); };

        for (int i = 0; i < 4; i++)
        {
            int c = i;
            var b = _headers[i] = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 26, Padding = new Thickness(0), [Grid.ColumnProperty] = i, Foreground = Dialogs.Res("Text2") };
            b.Click += (_, _) => SetSort(ColSorts[c]);
            Header.Children.Add(b);
        }
        List.SelectionChanged += (_, _) => OnSelectionChanged();
        List.DoubleTapped += (_, e) => { if (ItemAt(e.Source) is { } it) Open(it, e.KeyModifiers.HasFlag(KeyModifiers.Control)); };
        List.PointerPressed += List_PointerPressed;
        List.PointerReleased += List_PointerReleased;
        List.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || _s.View == ViewMode.Details) return;
            _s.IconSize = Math.Clamp(_s.IconSize + (e.Delta.Y > 0 ? 8 : -8), 40, 192); ApplyView(); e.Handled = true;
        };
        Crumbs.SizeChanged += (_, _) => CrumbScroll.ScrollToEnd();
        Opened += (_, _) => List.Focus();
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            _ctrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control); // Click events carry no modifiers
            var p = e.GetCurrentPoint(this).Properties;
            if (p.IsXButton1Pressed) { _cur?.GoBack(); e.Handled = true; } else if (p.IsXButton2Pressed) { _cur?.GoForward(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Closing += (_, _) =>
        {
            _s.Maximized = WindowState == WindowState.Maximized;
            if (!_s.Maximized) { _s.WindowWidth = Width; _s.WindowHeight = Height; }
            _s.OpenTabs = _tabs.Select(t => t.Path).ToList();
            _s.ActiveTab = _cur == null ? 0 : _tabs.IndexOf(_cur);
            Platform.SaveSettings(_s);
            foreach (var t in _tabs) t.Dispose();
        };

        ApplyView();
        foreach (var p in _s.OpenTabs) if (Directory.Exists(p)) AddTab(p);
        if (startPath != null) { AddTab(startPath); Activate(_tabs[^1]); }
        else if (_tabs.Count == 0) { AddTab(Platform.Home); Activate(_tabs[0]); }
        else Activate(_tabs[Math.Clamp(_s.ActiveTab, 0, _tabs.Count - 1)]);
        LoadSidebar();
        PreviewPane.IsVisible = _s.PreviewPane;
    }

    // ================= small helpers =================
    public void Notify(string text)
    {
        NoticeText.Text = text;
        _noticeTimer.Stop(); _noticeTimer.Start();
    }

    static Shape Glyph(Geometry g, IBrush fill, double size) => new() { Data = g, Fill = fill, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };

    static MenuItem MI(string header, Action click, string? gesture = null, bool enabled = true, bool? check = null)
    {
        var g = new Grid { ColumnDefinitions = new("*,Auto"), MinWidth = 200 };
        g.Children.Add(new TextBlock { Text = header });
        g.Children.Add(new TextBlock { Text = gesture ?? "", Foreground = Dialogs.Res("Text3"), Margin = new Thickness(24, 0, 0, 0), [Grid.ColumnProperty] = 1 });
        var m = new MenuItem { Header = g, IsEnabled = enabled };
        if (check is bool c) { m.ToggleType = MenuItemToggleType.CheckBox; m.IsChecked = c; }
        m.Click += (_, _) => click();
        return m;
    }

    static void Flyout(Control anchor, IEnumerable<Control> items, bool atPointer = false)
    {
        var f = new MenuFlyout { ItemsSource = items.ToList() };
        f.ShowAt(anchor, atPointer);
    }

    IReadOnlyList<Item> Sel => _cur?.Selection ?? Array.Empty<Item>();

    // ================= tabs =================
    void AddTab(string path, int? index = null)
    {
        var t = new Tab(_s, Notify, path);
        t.Rebuilt += OnRebuilt;
        t.Navigated += tab => { RecentList.Add(_s.RecentFolders, tab.Path); if (tab == _cur) { SearchBox.Text = ""; UpdateNav(); UpdateSidebarCurrent(); } RebuildTabs(); };
        t.StatusChanged += tab => { if (tab == _cur) UpdateStatus(); };
        if (index is int i && i <= _tabs.Count) _tabs.Insert(i, t); else _tabs.Add(t);
        RebuildTabs();
    }

    void NewTab(string? path = null, bool activate = true)
    {
        var p = path != null && Directory.Exists(path) ? path : Platform.Home;
        RecentList.Add(_s.RecentFolders, p);
        AddTab(p);
        if (activate) Activate(_tabs[^1]);
    }

    void Activate(Tab t)
    {
        if (_cur == t) return;
        _cur = t;
        SearchBox.Text = "";
        t.EnsureLoaded();
        RebuildTabs(); UpdateNav(); UpdateSidebarCurrent();
        OnRebuilt(t); // show what the tab already has
        UpdatePreview();
    }

    void CloseTab(Tab? t)
    {
        if (t == null) return;
        if (_tabs.Count <= 1) { Close(); return; }
        var i = _tabs.IndexOf(t);
        _closed.Push(t.Path);
        _tabs.Remove(t); t.Dispose();
        if (_cur == t) { _cur = null; Activate(_tabs[Math.Min(i, _tabs.Count - 1)]); }
        RebuildTabs();
    }

    void CycleTab(int dir) { if (_cur != null && _tabs.Count > 1) Activate(_tabs[(_tabs.IndexOf(_cur) + dir + _tabs.Count) % _tabs.Count]); }

    void RebuildTabs()
    {
        TabStrip.Children.Clear();
        foreach (var t in _tabs)
        {
            var tab = t;
            var title = tab.Title.Length > 22 ? tab.Title[..21] + "…" : tab.Title;
            var close = new Button { Content = "×", Padding = new Thickness(6, 0), MinHeight = 22, CornerRadius = new CornerRadius(11) };
            close.Click += (_, e) => { CloseTab(tab); e.Handled = true; };
            var b = new Button { Classes = { "tab" }, MinWidth = 100, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, close } } };
            if (tab == _cur) b.Classes.Add("on");
            b.Click += (_, _) => Activate(tab);
            b.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle) CloseTab(tab);
                else if (e.InitialPressMouseButton == MouseButton.Right)
                    Flyout(b, new Control[]
                    {
                        MI("Duplicate tab", () => { AddTab(tab.Path, _tabs.IndexOf(tab) + 1); Activate(_tabs[_tabs.IndexOf(tab) + 1]); }),
                        MI("Close tab", () => CloseTab(tab), "Ctrl+W"),
                        MI("Close other tabs", () => { foreach (var o in _tabs.Where(x => x != tab).ToList()) CloseTab(o); }, enabled: _tabs.Count > 1),
                        MI("Reopen closed tab", ReopenClosedTab, "Ctrl+Shift+T"),
                    }, atPointer: true);
            };
            TabStrip.Children.Add(b);
        }
    }

    void ReopenClosedTab() { if (_closed.Count > 0) NewTab(_closed.Pop()); }

    // ================= navigation row =================
    void Refresh() { _cur?.Refresh(); LoadSidebar(); }

    void UpdateNav()
    {
        var t = _cur; if (t == null) return;
        BackButton.IsEnabled = t.CanGoBack; ForwardButton.IsEnabled = t.CanGoForward;
        Crumbs.Children.Clear();
        foreach (var (name, path) in PathUtil.Segments(t.Path))
        {
            var p = path;
            var b = new Button { Content = name, Padding = new Thickness(6, 1), MinHeight = 26, CornerRadius = new CornerRadius(8) };
            b.Click += (_, _) => { if (_ctrlDown) NewTab(p); else _cur?.Navigate(p); };
            if (Crumbs.Children.Count > 0) Crumbs.Children.Add(new TextBlock { Text = "›", Foreground = Dialogs.Res("Text3"), VerticalAlignment = VerticalAlignment.Center });
            Crumbs.Children.Add(b);
        }
        UpdateStatus();
    }

    void BeginAddressEdit()
    {
        AddressBox.Text = _cur?.Path ?? "";
        CrumbScroll.IsVisible = false; AddressBox.IsVisible = true;
        AddressBox.Focus(); AddressBox.SelectAll();
    }

    void EndAddressEdit() { AddressBox.IsVisible = false; CrumbScroll.IsVisible = true; }

    void AddressKey(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndAddressEdit(); List.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = Environment.ExpandEnvironmentVariables((AddressBox.Text ?? "").Trim().Trim('"'));
            if (text == "~" || text.StartsWith("~/")) text = Platform.Home + text[1..];
            EndAddressEdit();
            if (text.Length > 0)
            {
                if (Directory.Exists(text)) _cur?.Navigate(Path.GetFullPath(text));
                else if (File.Exists(text)) OpenPath(text);
                else Notify($"Can't find \"{text}\".");
            }
            List.Focus();
        }
    }

    void SearchKey(object? s, KeyEventArgs e)
    {
        if (_cur == null) return;
        if (e.Key == Key.Enter) { _cur.StartSearch(SearchBox.Text ?? ""); e.Handled = true; List.Focus(); }
        else if (e.Key == Key.Escape) { SearchBox.Text = ""; _cur.CancelSearch(); e.Handled = true; List.Focus(); }
    }

    // ================= sidebar =================
    void LoadSidebar()
    {
        Side.Children.Clear();
        void Header(string t, string? hint = null) => Side.Children.Add(new TextBlock { Text = t.ToUpperInvariant(), FontSize = 11, Foreground = Dialogs.Res("Text3"), Margin = new Thickness(8, 10, 0, 2) });
        Header("Quick access");
        foreach (var (name, path) in Platform.QuickAccess()) Side.Children.Add(SideItem(name, path, null, false));
        Header("Pinned");
        if (_s.Pinned.Count == 0) Side.Children.Add(new TextBlock { Text = "Right-click a folder and choose \"Pin to sidebar\"", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Dialogs.Res("Text3"), Margin = new Thickness(8, 0) });
        foreach (var p in _s.Pinned) Side.Children.Add(SideItem(Path.GetFileName(p.TrimEnd('/')) is { Length: > 0 } n ? n : p, p, null, true));
        Header("Drives");
        foreach (var (name, path, free) in Platform.Drives()) Side.Children.Add(SideItem(name, path, free, false));
        UpdateSidebarCurrent();
    }

    Button SideItem(string name, string path, string? sub, bool pinned)
    {
        var text = new StackPanel { Children = { new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis } } };
        if (!string.IsNullOrEmpty(sub)) text.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = Dialogs.Res("Text3") });
        var geo = Glyphs.For(new FileEntry(name, path, true, 0, default, default, FileAttributes.Directory));
        var b = new Button { Classes = { "side" }, Tag = path, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Glyph(geo.Geo, geo.Fill, 16), text } } };
        b.Click += (_, _) => { if (_ctrlDown) NewTab(path); else if (Directory.Exists(path)) _cur?.Navigate(path); else Notify($"Can't find \"{path}\"."); };
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle) NewTab(path, activate: false);
            else if (e.InitialPressMouseButton == MouseButton.Right)
                Flyout(b, new Control[]
                {
                    MI("Open", () => _cur?.Navigate(path)),
                    MI("Open in new tab", () => NewTab(path, activate: false)),
                    MI(pinned || IsPinned(path) ? "Unpin from sidebar" : "Pin to sidebar", () => TogglePin(path)),
                    MI("Open in terminal", () => OpenTerminal(path)),
                }, atPointer: true);
        };
        return b;
    }

    void UpdateSidebarCurrent()
    {
        foreach (var b in Side.Children.OfType<Button>()) b.Classes.Set("on", Platform.Same(b.Tag as string, _cur?.Path));
    }

    bool IsPinned(string path) => _s.Pinned.Any(x => Platform.Same(x, path));

    void TogglePin(string path)
    {
        if (_s.Pinned.FirstOrDefault(x => Platform.Same(x, path)) is { } e) _s.Pinned.Remove(e); else _s.Pinned.Add(path);
        Platform.SaveSettings(_s);
        LoadSidebar();
    }

    // ================= view / sort =================
    void SetView(ViewMode m)
    {
        _s.View = m;
        if (m != ViewMode.Details) _s.IconSize = m == ViewMode.Tiles ? 64 : m == ViewMode.Large ? 112 : 160;
        ApplyView();
    }

    void ApplyView()
    {
        bool details = _s.View == ViewMode.Details;
        Header.IsVisible = details;
        if (details)
        {
            List.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
            List.ItemTemplate = new FuncDataTemplate<Item>((it, _) =>
            {
                if (it == null) return null;
                var g = new Grid { ColumnDefinitions = new("*,150,120,90"), Background = Brushes.Transparent };
                g.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Glyph(it.Glyph, it.Tint, 18), new TextBlock { Text = it.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } } });
                string[] cols = { it.ModifiedText, it.TypeText, it.SizeText };
                for (int i = 0; i < 3; i++) g.Children.Add(new TextBlock { Text = cols[i], Foreground = Dialogs.Res("Text2"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = i + 1 });
                return g;
            });
        }
        else
        {
            double size = _s.IconSize;
            // shortcut: WrapPanel is not virtualized, so folders with tens of thousands of files are slow in icon views; use Details there.
            List.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel());
            List.ItemTemplate = new FuncDataTemplate<Item>((it, _) => it == null ? null : new StackPanel
            {
                Width = size + 28, Spacing = 6, Background = Brushes.Transparent,
                Children =
                {
                    Glyph(it.Glyph, it.Tint, size),
                    new TextBlock { Text = it.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center },
                }
            });
        }
        UpdateHeaders();
    }

    void SetSort(SortColumn col)
    {
        if (_s.SortColumn == col) _s.SortDescending = !_s.SortDescending; else { _s.SortColumn = col; _s.SortDescending = false; }
        ResortAll();
    }

    void ResortAll() { UpdateHeaders(); foreach (var t in _tabs) t.Resort(); }

    void UpdateHeaders()
    {
        for (int i = 0; i < 4; i++)
            _headers[i].Content = ColNames[i] + (ColSorts[i] == _s.SortColumn ? (_s.SortDescending ? "  ▼" : "  ▲") : "");
    }

    // ================= list =================
    void OnRebuilt(Tab t)
    {
        if (t != _cur) return;
        var pending = t.TakePendingSelect();
        List.ItemsSource = t.Items;
        OverlayText.Text = t.Overlay;
        if (pending != null)
        {
            var items = t.Items.Where(i => pending.Any(p => Platform.Same(p, i.FullPath))).ToList();
            foreach (var i in items) List.SelectedItems!.Add(i);
            if (items.Count > 0) List.ScrollIntoView(items[0]);
        }
        UpdateStatus();
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox) List.Focus();
    }

    void UpdateStatus()
    {
        var t = _cur; if (t == null) return;
        CountText.Text = t.IsSearching ? "Searching…" : t.IsLoading ? "Loading…" : t.CountText;
        FreeText.Text = t.FreeText;
    }

    void OnSelectionChanged()
    {
        if (_cur == null) return;
        _cur.Selection = List.SelectedItems!.Cast<Item>().ToList();
        long bytes = Sel.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size);
        SelectionText.Text = Sel.Count == 0 ? "" : Sel.Count + " selected" + (bytes > 0 ? $" ({Format.Size(bytes)})" : "");
        UpdatePreview();
    }

    static Item? ItemAt(object? source) => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as Item;

    void List_PointerPressed(object? s, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(List).Properties;
        var it = ItemAt(e.Source);
        if (p.IsRightButtonPressed)
        {
            if (it == null) List.SelectedItems!.Clear();
            else if (!List.SelectedItems!.Contains(it)) { List.SelectedItems.Clear(); List.SelectedItems.Add(it); }
            e.Handled = true;
        }
        else if (p.IsMiddleButtonPressed && it is { IsDirectory: true }) { NewTab(it.FullPath, activate: false); e.Handled = true; }
        else if (p.IsLeftButtonPressed && it == null && e.KeyModifiers == KeyModifiers.None) List.SelectedItems!.Clear();
    }

    void List_PointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        e.Handled = true;
        Flyout(List, ContextMenuItems(ItemAt(e.Source) != null), atPointer: true);
    }

    // ================= menus =================
    IEnumerable<Control> NewMenuItems() => new Control[] { MI("Folder", NewFolder, "Ctrl+Shift+N"), MI("Text document", NewTextFile) };

    IEnumerable<Control> SortMenuItems()
    {
        foreach (var (name, col) in new[] { ("Name", SortColumn.Name), ("Date modified", SortColumn.Modified), ("Type", SortColumn.Type), ("Size", SortColumn.Size) })
        { var c = col; yield return MI(name, () => { _s.SortColumn = c; ResortAll(); }, check: _s.SortColumn == c); }
        yield return new Separator();
        yield return MI("Ascending", () => { _s.SortDescending = false; ResortAll(); }, check: !_s.SortDescending);
        yield return MI("Descending", () => { _s.SortDescending = true; ResortAll(); }, check: _s.SortDescending);
    }

    IEnumerable<Control> ViewMenuItems()
    {
        var names = new[] { "Details", "Tiles", "Large icons", "Extra large icons" };
        for (int i = 0; i < 4; i++) { var m = (ViewMode)i; yield return MI(names[i], () => SetView(m), $"Ctrl+Shift+{i + 1}", check: _s.View == m); }
    }

    IEnumerable<Control> MoreMenuItems()
    {
        foreach (var m in UndoItems()) yield return m;
        yield return new Separator();
        yield return MI("Show hidden items", ToggleHidden, "Ctrl+H", check: _s.ShowHidden);
        yield return MI("Preview pane", TogglePreview, "Alt+P", check: _s.PreviewPane);
        yield return MI("Open terminal here", () => OpenTerminal(null));
        yield return new Separator();
        yield return MI("Copy path", CopyPath, "Ctrl+Shift+C");
        yield return MI("Open Trash", Platform.OpenTrash);
        if (Platform.IsInstalled)
        {
            yield return new Separator();
            yield return Platform.IsDefaultFileManager()
                ? MI("Restore previous default file manager", () => Notify(Platform.RestoreDefaultFileManager() ?? "Folders open in your previous file manager again."))
                : MI("Make Frost the default file manager", () => Notify(Platform.MakeDefaultFileManager() ?? "Frost is now your default file manager."));
        }
    }

    IEnumerable<Control> ContextMenuItems(bool onItem)
    {
        var t = _cur;
        if (t == null) yield break;
        var sel = t.Selection;
        if (onItem && sel.Count > 0)
        {
            var one = sel.Count == 1 ? sel[0] : null;
            yield return MI("Open", OpenSelection, "Enter");
            if (one is { IsDirectory: true }) yield return MI("Open in new tab", () => NewTab(one.FullPath, activate: false));
            yield return new Separator();
            yield return MI("Cut", () => CopySelection(true), "Ctrl+X");
            yield return MI("Copy", () => CopySelection(false), "Ctrl+C");
            if (one is { IsDirectory: true }) yield return MI("Paste into folder", () => Paste(one.FullPath), enabled: _clip.Count > 0);
            yield return MI("Copy path", CopyPath, "Ctrl+Shift+C");
            yield return MI(one != null ? "Rename" : "Rename…", RenameSelection, "F2");
            yield return MI("Move to trash", () => DeleteSelection(false), "Del");
            yield return new Separator();
            yield return MI("Compress to ZIP", CompressSelection);
            if (one is { IsDirectory: false } && Archives.IsZip(one.Name)) { yield return MI("Extract here", () => ExtractZip(one)); }
            if (one is { IsDirectory: true }) yield return MI(IsPinned(one.FullPath) ? "Unpin from sidebar" : "Pin to sidebar", () => TogglePin(one.FullPath));
            yield return MI("Open in terminal", () => OpenTerminal(one is { IsDirectory: true } ? one.FullPath : t.Path));
        }
        else
        {
            yield return new MenuItem { Header = "View", ItemsSource = ViewMenuItems().ToList() };
            yield return new MenuItem { Header = "Sort by", ItemsSource = SortMenuItems().ToList() };
            yield return new Separator();
            yield return MI("New folder", NewFolder, "Ctrl+Shift+N");
            yield return MI("New text document", NewTextFile);
            yield return MI("Paste", () => Paste(), "Ctrl+V", enabled: _clip.Count > 0);
            yield return new Separator();
            yield return MI("Open in terminal", () => OpenTerminal(null));
            yield return MI("Refresh", Refresh, "F5");
        }
    }

    void ToggleHidden() { _s.ShowHidden = !_s.ShowHidden; foreach (var t in _tabs) if (t.Loaded) t.Refresh(); }
    void TogglePreview() { _s.PreviewPane = !_s.PreviewPane; PreviewPane.IsVisible = _s.PreviewPane; if (_s.PreviewPane) UpdatePreview(); }
    void OpenTerminal(string? folder) { folder ??= _cur?.Path; if (folder == null) return; try { Platform.OpenTerminal(folder); } catch (Exception ex) { Notify("Couldn't open a terminal: " + ex.Message); } }

    void OpenPath(string path) { try { Platform.Open(path); } catch (Exception ex) { Notify("Couldn't open file: " + ex.Message); } }

    void Open(Item it, bool newTab = false)
    {
        if (it.IsDirectory) { if (newTab) NewTab(it.FullPath, activate: false); else _cur?.Navigate(it.FullPath); }
        else OpenPath(it.FullPath);
    }

    void OpenSelection()
    {
        if (Sel.Count == 1) Open(Sel[0]);
        else foreach (var i in Sel.ToList()) Open(i, newTab: true);
    }

    // ================= preview pane =================
    void UpdatePreview() { if (_s.PreviewPane) { _previewTimer.Stop(); _previewTimer.Start(); } }

    async Task ShowPreview()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var ct = cts.Token;
        PrevImage.IsVisible = false; PrevImage.Source = null; PrevGlyph.IsVisible = true; PrevText.Text = "";
        var sel = Sel;
        try
        {
            if (sel.Count == 0)
            {
                if (_cur == null) return;
                PrevTitle.Text = _cur.Title; PrevInfo.Text = "Folder\n" + _cur.CountText;
                var g = Glyphs.For(new FileEntry("", _cur.Path, true, 0, default, default, FileAttributes.Directory));
                PrevGlyph.Data = g.Geo; PrevGlyph.Fill = g.Fill;
                return;
            }
            if (sel.Count > 1)
            {
                PrevTitle.Text = sel.Count + " items selected"; PrevInfo.Text = Format.Size(sel.Where(i => !i.IsDirectory).Sum(i => i.Entry.Size));
                PrevGlyph.Data = sel[0].Glyph; PrevGlyph.Fill = sel[0].Tint;
                return;
            }
            var it = sel[0];
            PrevTitle.Text = it.Name;
            PrevGlyph.Data = it.Glyph; PrevGlyph.Fill = it.Tint;
            string head = $"{it.TypeText}\nModified: {it.ModifiedText}\nCreated: {Format.Date(it.Entry.Created, DateTime.Now)}\n";
            PrevInfo.Text = head;
            if (it.IsDirectory)
            {
                PrevInfo.Text = head + "Calculating…";
                var m = await FileOperations.MeasureAsync(new[] { it.FullPath }, ct);
                if (!ct.IsCancellationRequested) PrevInfo.Text = head + $"{Format.Size(m.Bytes)} · {m.Files} files, {m.Folders} folders";
                return;
            }
            PrevInfo.Text = head + Format.Size(it.Entry.Size);
            if (Archives.IsZip(it.Name))
            {
                var z = await Task.Run(() => Archives.Describe(it.FullPath, 200), ct);
                if (ct.IsCancellationRequested) return;
                PrevInfo.Text = head + $"{Format.Size(it.Entry.Size)} · {z.Count} entries, {Format.Size(z.Bytes)} uncompressed";
                PrevText.Text = string.Join('\n', z.Names);
                return;
            }
            if (ImageExt.Contains(it.Entry.Extension.ToLowerInvariant()) && it.Entry.Size < 40_000_000)
            {
                var bmp = await Task.Run(() => { try { using var fs = File.OpenRead(it.FullPath); return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 480); } catch { return null; } }, ct);
                if (!ct.IsCancellationRequested && bmp != null) { PrevImage.Source = bmp; PrevImage.IsVisible = true; PrevGlyph.IsVisible = false; return; }
            }
            if (it.Entry.Size < 1_000_000)
            {
                var text = await Task.Run(() => ReadText(it.FullPath));
                if (!ct.IsCancellationRequested && text != null) PrevText.Text = text;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    static readonly HashSet<string> ImageExt = new() { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico" };

    static string? ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(65536, fs.Length)];
            var n = fs.Read(buf, 0, buf.Length);
            if (Array.IndexOf(buf, (byte)0, 0, Math.Min(n, 8000)) >= 0) return null; // binary
            return string.Join('\n', System.Text.Encoding.UTF8.GetString(buf, 0, n).Split('\n').Take(200).Select(l => l.TrimEnd('\r')));
        }
        catch { return null; }
    }

    // ================= keyboard =================
    void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool inText = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        bool onButton = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button;
        var t = _cur;
        bool h = true;
        if (!inText && ctrl && !alt && shift && key == Key.P) ShowCommandPalette();
        else if (!inText && ctrl && !alt && !shift && key == Key.P) ShowGoToFolder();
        else if (ctrl && !alt && !shift && key == Key.T) NewTab();
        else if (ctrl && !alt && shift && key == Key.T) ReopenClosedTab();
        else if (ctrl && !alt && !shift && key == Key.W) CloseTab(t);
        else if (ctrl && !alt && key == Key.Tab) CycleTab(shift ? -1 : 1);
        else if ((ctrl && !alt && !shift && key == Key.L) || (alt && !ctrl && key == Key.D) || (!ctrl && !alt && !shift && key == Key.F4)) BeginAddressEdit();
        else if (ctrl && !alt && !shift && (key == Key.F || key == Key.E)) { SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (alt && !ctrl && key == Key.P) TogglePreview();
        else if (alt && !ctrl && key == Key.Left) t?.GoBack();
        else if (alt && !ctrl && key == Key.Right) t?.GoForward();
        else if (alt && !ctrl && key == Key.Up) t?.Up();
        else if (!ctrl && !alt && key == Key.F5 || ctrl && !alt && !shift && key == Key.R) Refresh();
        else if (ctrl && !alt && shift && key == Key.N) NewFolder();
        else if (ctrl && !alt && !shift && key == Key.H) ToggleHidden();
        else if (ctrl && !alt && shift && key >= Key.D1 && key <= Key.D4) SetView((ViewMode)(key - Key.D1));
        else if (inText) h = false;
        else if (ctrl && !alt && !shift && key == Key.C) CopySelection(false);
        else if (ctrl && !alt && !shift && key == Key.X) CopySelection(true);
        else if (ctrl && !alt && !shift && key == Key.V) Paste();
        else if (ctrl && !alt && ((!shift && key == Key.Y) || (shift && key == Key.Z))) _ = RunUndo(redo: true);
        else if (ctrl && !alt && !shift && key == Key.Z) _ = RunUndo(redo: false);
        else if (ctrl && !alt && shift && key == Key.C) CopyPath();
        else if (ctrl && !alt && !shift && key == Key.A) List.SelectAll();
        else if (!ctrl && !alt && key == Key.Delete) DeleteSelection(shift);
        else if (!ctrl && !alt && !shift && key == Key.F2) RenameSelection();
        else if (alt && !ctrl && key == Key.Enter) { if (!_s.PreviewPane) TogglePreview(); } // properties = the preview pane
        else if (!ctrl && !alt && !shift && key == Key.Enter && !onButton && List.IsKeyboardFocusWithin) OpenSelection();
        else if (!ctrl && !alt && !shift && key == Key.Back && !onButton) t?.GoBack();
        else h = false;
        if (h) e.Handled = true;
    }
}
