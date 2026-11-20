using System.Diagnostics;
using System.Windows.Automation;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private ListBox? _lastList;
    private Point _dragStart;
    private ItemViewModel? _dragItem, _pendingClick, _dropHover;
    private bool _dragReady;
    private int _anchor;

    private static readonly string[] ColumnNames = { "Name", "Date modified", "Type", "Size" };
    private static readonly SortColumn[] ColumnSorts = { SortColumn.Name, SortColumn.Modified, SortColumn.Type, SortColumn.Size };

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(SettingsStore.Load());
        DataContext = _vm;
        _vm.Owner = this;
        _vm.ItemsRebuilt += OnItemsRebuilt;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Tabs.CollectionChanged += (_, _) => UpdateTabWidth();
        ApplySavedBounds();
        UpdateHeaders();
    }

    private MainViewModel Vm => _vm;
    private ListBox ActiveList => _vm.IsDetails ? DetailsList : IconList;
    private static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");

    // ================= window plumbing =================
    private void ApplySavedBounds()
    {
        var s = _vm.Settings;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.Contains(new Point(s.WindowLeft + 60, s.WindowTop + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft; Top = s.WindowTop; Width = s.WindowWidth; Height = s.WindowHeight;
            }
        }
        if (s.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        bool ok = false;
        try { ok = Backdrop.TryApply(this, true); } catch { }
        Root.Background = ok ? (Brush)FindResource("WindowTintBrush") : (Brush)FindResource("FallbackBackgroundBrush");
        ApplyMaximizedPadding();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.Start(App.StartPath);
        UpdateTabWidth();
        if (App.StartNotice != null) _vm.Notify(App.StartNotice);
        if (!Installer.IsInstalled) return; // portable copies only check for updates from the menu
        var ask = !_vm.Settings.AskedDefault && !DefaultFileManager.IsEnabled();
        var check = DateTime.UtcNow - _vm.Settings.LastUpdateCheck >= TimeSpan.FromHours(24);
        if (ask || check)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { if (ask) AskDefault(); if (check) CheckForUpdates(false); });
    }

    /// <summary>Manual checks ignore the 24 h limit and the skipped version, and report "up to date" / errors. Never throws.</summary>
    private async void CheckForUpdates(bool manual)
    {
        var s = _vm.Settings;
        try
        {
            if (!manual) { s.LastUpdateCheck = DateTime.UtcNow; SettingsStore.Save(s); }
            var u = await Updater.CheckAsync();
            if (u == null || (!manual && u.Tag == s.SkippedVersion))
            {
                if (manual) _vm.Notify("You're up to date.");
                return;
            }
            var version = u.Version.ToString(3);
            var body = u.Body.Length > 600 ? u.Body[..600].TrimEnd() + "\u2026" : u.Body;
            var choice = DialogWindow.Show(this, $"Frost {version} is available (you have {Updater.Current.ToString(3)})", body,
                new[] { "Download and install", "Skip this version", "Later" }, null, out _);
            if (choice == 1) { s.SkippedVersion = u.Tag; SettingsStore.Save(s); }
            if (choice != 0) return;

            string? file = null;
            var error = DialogWindow.RunWithProgress(this, $"Downloading Frost {version}", "The installer starts when the download is done.",
                async (p, ct) => file = await Updater.DownloadAsync(u, p, ct));
            if (error is OperationCanceledException) return;
            if (error != null) { DialogWindow.Show(this, "Couldn't download the update", error.Message, new[] { "OK" }, null, out _); return; }

            Process.Start(new ProcessStartInfo(file!) { UseShellExecute = false, ArgumentList = { "--install", "--wait-pid", Environment.ProcessId.ToString() } });
            Close(); // saves settings and exits; setup waits for this process before copying
        }
        catch (Exception ex)
        {
            if (manual) _vm.Notify("Couldn't check for updates: " + ex.Message);
        }
    }

    private void AskDefault()
    {
        _vm.Settings.AskedDefault = true;
        SettingsStore.Save(_vm.Settings);
        if (DialogWindow.Show(this, "Make Frost your default file manager?",
                "Folders, drives and Win+E will open in Frost. You can undo this anytime from the \u2026 menu.",
                new[] { "Yes", "Not now" }, null, out _) == 0)
            _vm.SetDefaultFileManager(true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var s = _vm.Settings;
        s.Maximized = WindowState == WindowState.Maximized;
        var b = RestoreBounds_();
        s.WindowLeft = b.Left; s.WindowTop = b.Top; s.WindowWidth = b.Width; s.WindowHeight = b.Height;
        _vm.SaveState();
        foreach (var t in _vm.Tabs) t.Dispose();
    }

    private Rect RestoreBounds_() => WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : base.RestoreBounds;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        ApplyMaximizedPadding();
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        AutomationProperties.SetName(MaxButton, WindowState == WindowState.Maximized ? "Restore" : "Maximize");
    }

    private void ApplyMaximizedPadding()
    {
        // WindowChrome over-extends the client area by the resize border when maximized.
        Root.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();

    private void UpdateTabWidth()
    {
        int n = Math.Max(1, _vm.Tabs.Count);
        _vm.TabWidth = Math.Clamp((ActualWidth - 260) / n, 90, 200);
    }

    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { _vm.CurrentTab?.GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { _vm.CurrentTab?.GoForward(); e.Handled = true; }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            var old = _lastList;
            var now = ActiveList;
            if (old != null && old != now)
            {
                var sel = old.SelectedItems.Cast<object>().ToList();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    now.SelectedItems.Clear();
                    foreach (var i in sel) now.SelectedItems.Add(i);
                    now.Focus();
                }));
            }
            _lastList = now;
        }
        else if (e.PropertyName == "Sort") UpdateHeaders();
        else if (e.PropertyName == nameof(MainViewModel.CurrentTab)) ScrollCrumbs();
    }

    private void ScrollCrumbs() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => CrumbScroll.ScrollToRightEnd()));

    // ================= tabs =================
    private void NewTab_Click(object s, RoutedEventArgs e) => _vm.NewTab();

    private void Tab_LeftDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel t } && !(e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null))
            _vm.Activate(t);
    }

    private void Tab_MiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: TabViewModel t }) { _vm.CloseTab(t); e.Handled = true; }
    }

    private void Tab_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel t } fe) return;
        var m = new ContextMenu();
        m.Items.Add(MI("Duplicate tab", "", () => _vm.DuplicateTab(t)));
        m.Items.Add(MI("Close tab", "", () => _vm.CloseTab(t), "Ctrl+W"));
        m.Items.Add(MI("Close other tabs", null, () => { foreach (var o in _vm.Tabs.Where(x => x != t).ToList()) _vm.CloseTab(o); }, enabled: _vm.Tabs.Count > 1));
        m.Items.Add(MI("Reopen closed tab", "", _vm.ReopenClosedTab, "Ctrl+Shift+T"));
        m.PlacementTarget = fe; m.Placement = PlacementMode.MousePoint; m.IsOpen = true;
        e.Handled = true;
    }

    // ================= navigation row =================
    private void Back_Click(object s, RoutedEventArgs e) => _vm.CurrentTab?.GoBack();
    private void Forward_Click(object s, RoutedEventArgs e) => _vm.CurrentTab?.GoForward();
    private void Up_Click(object s, RoutedEventArgs e) => _vm.CurrentTab?.Up();
    private void Refresh_Click(object s, RoutedEventArgs e) { _vm.CurrentTab?.Refresh(); _vm.LoadDrives(); }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) _vm.OpenInNewTab(path); else _vm.CurrentTab?.Navigate(path);
    }

    private void Address_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && AddressBox.Visibility != Visibility.Visible) { BeginAddressEdit(); e.Handled = true; }
    }

    private void BeginAddressEdit()
    {
        AddressBox.Text = _vm.CurrentTab?.Path ?? "";
        CrumbScroll.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.UpdateLayout();
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
        ScrollCrumbs();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndAddressEdit(); FocusList(); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = Environment.ExpandEnvironmentVariables(AddressBox.Text.Trim().Trim('"'));
            EndAddressEdit();
            if (text.Length == 0) return;
            try
            {
                if (Directory.Exists(text)) _vm.CurrentTab?.Navigate(text);
                else if (File.Exists(text)) ShellActions.Open(text);
                else _vm.Notify($"Can't find \"{text}\".");
            }
            catch (Exception ex) { _vm.Notify(ex.Message); }
            FocusList();
        }
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (AddressBox.Visibility == Visibility.Visible) EndAddressEdit(); }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        var t = _vm.CurrentTab;
        if (t == null) return;
        if (e.Key == Key.Enter) { t.StartSearch(SearchBox.Text); e.Handled = true; FocusList(); }
        else if (e.Key == Key.Escape) { t.SearchText = ""; t.CancelSearch(); e.Handled = true; FocusList(); }
    }

    private void FocusList()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var l = ActiveList;
            if (l.SelectedIndex < 0 && l.Items.Count > 0) { l.Focus(); return; }
            if (l.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, l.SelectedIndex)) is ListBoxItem i) i.Focus(); else l.Focus();
        }));
    }

    // ================= menus =================
    private static MenuItem MI(string header, string? glyph, Action click, string? gesture = null, bool check = false, bool isChecked = false, bool enabled = true)
    {
        var m = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsCheckable = check, IsChecked = isChecked, IsEnabled = enabled };
        if (glyph != null) m.Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14 };
        m.Click += (_, _) => click();
        return m;
    }

    private static void ShowMenu(FrameworkElement anchor, ContextMenu m)
    {
        m.PlacementTarget = anchor; m.Placement = PlacementMode.Bottom; m.IsOpen = true;
    }

    private void New_Click(object s, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MI("Folder", "", _vm.NewFolder, "Ctrl+Shift+N"));
        m.Items.Add(MI("Text document", "", _vm.NewTextFile));
        ShowMenu(NewButton, m);
    }

    private ContextMenu BuildSortMenu()
    {
        var m = new ContextMenu();
        var st = _vm.Settings;
        foreach (var (name, col) in new[] { ("Name", SortColumn.Name), ("Date modified", SortColumn.Modified), ("Type", SortColumn.Type), ("Size", SortColumn.Size) })
        {
            var c = col;
            m.Items.Add(MI(name, null, () => { st.SortColumn = c; _vm.SetSortDirection(st.SortDescending); }, check: true, isChecked: st.SortColumn == c));
        }
        m.Items.Add(new Separator());
        m.Items.Add(MI("Ascending", "", () => _vm.SetSortDirection(false), check: true, isChecked: !st.SortDescending));
        m.Items.Add(MI("Descending", "", () => _vm.SetSortDirection(true), check: true, isChecked: st.SortDescending));
        return m;
    }

    private ContextMenu BuildViewMenu()
    {
        var m = new ContextMenu();
        var v = _vm.ViewMode;
        m.Items.Add(MI("Details", "", () => _vm.SetView(ViewMode.Details), "Ctrl+Shift+1", true, v == ViewMode.Details));
        m.Items.Add(MI("Tiles", "", () => _vm.SetView(ViewMode.Tiles), "Ctrl+Shift+2", true, v == ViewMode.Tiles));
        m.Items.Add(MI("Large icons", "", () => _vm.SetView(ViewMode.Large), "Ctrl+Shift+3", true, v == ViewMode.Large));
        m.Items.Add(MI("Extra large icons", "", () => _vm.SetView(ViewMode.ExtraLarge), "Ctrl+Shift+4", true, v == ViewMode.ExtraLarge));
        return m;
    }

    private void Sort_Click(object s, RoutedEventArgs e) => ShowMenu(SortButton, BuildSortMenu());
    private void View_Click(object s, RoutedEventArgs e) => ShowMenu(ViewButton, BuildViewMenu());

    private void More_Click(object s, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MI("Show hidden items", "", () => _vm.ShowHidden = !_vm.ShowHidden, "Ctrl+H", true, _vm.ShowHidden));
        m.Items.Add(MI("Preview pane", "", () => _vm.PreviewPane = !_vm.PreviewPane, "Alt+P", true, _vm.PreviewPane));
        m.Items.Add(MI("Open terminal here", "", () => _vm.OpenTerminal()));
        m.Items.Add(new Separator());
        m.Items.Add(MI("Copy path", "", _vm.CopyPath, "Ctrl+Shift+C"));
        m.Items.Add(MI("Properties", "", () => _vm.Properties(_vm.CurrentTab?.Path), "Alt+Enter"));
        m.Items.Add(new Separator());
        var isDefault = DefaultFileManager.IsEnabled();
        m.Items.Add(MI(isDefault ? "Restore Explorer as default" : "Make Frost the default file manager", "", () =>
        {
            if (!isDefault && !Installer.IsInstalled) _vm.Notify("Install Frost first (run Frost-Setup) to make it the default file manager.");
            else _vm.SetDefaultFileManager(!isDefault);
        }));
        m.Items.Add(MI("Check for updates", "", () => CheckForUpdates(true)));
        ShowMenu(MoreButton, m);
    }

    private void Cut_Click(object s, RoutedEventArgs e) => _vm.CopySelection(true);
    private void Copy_Click(object s, RoutedEventArgs e) => _vm.CopySelection(false);
    private void Paste_Click(object s, RoutedEventArgs e) => _vm.Paste();
    private void Rename_Click(object s, RoutedEventArgs e) => RenameSelection();
    private void Delete_Click(object s, RoutedEventArgs e) => _vm.Delete((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    private void Terminal_Click(object s, RoutedEventArgs e) => _vm.OpenTerminal();

    // ================= sidebar =================
    private void Side_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SidebarItem it }) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) _vm.OpenInNewTab(it.Path);
        else if (Directory.Exists(it.Path)) _vm.CurrentTab?.Navigate(it.Path);
        else _vm.Notify($"Can't find \"{it.Path}\".");
    }

    private void Side_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: SidebarItem it }) { _vm.OpenInNewTab(it.Path); e.Handled = true; }
    }

    private void Side_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SidebarItem it } fe) return;
        var m = new ContextMenu();
        m.Items.Add(MI("Open", "", () => _vm.CurrentTab?.Navigate(it.Path)));
        m.Items.Add(MI("Open in new tab", "", () => _vm.OpenInNewTab(it.Path)));
        if (!it.IsDrive) m.Items.Add(MI(it.IsPinned || _vm.IsPinned(it.Path) ? "Unpin from sidebar" : "Pin to sidebar", "", () => _vm.TogglePin(it.Path)));
        m.Items.Add(MI("Open in terminal", "", () => _vm.OpenTerminal(it.Path)));
        m.Items.Add(MI("Properties", "", () => _vm.Properties(it.Path)));
        m.PlacementTarget = fe; m.Placement = PlacementMode.MousePoint; m.IsOpen = true;
        e.Handled = true;
    }

    private void Side_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (sender is FrameworkElement { DataContext: SidebarItem it } && e.Data.GetData(DataFormats.FileDrop) is string[] src && src.Length > 0)
            e.Effects = DecideEffect(src, it.Path, e);
        e.Handled = true;
    }

    private void Side_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SidebarItem it } && e.Data.GetData(DataFormats.FileDrop) is string[] src && src.Length > 0)
        {
            var eff = DecideEffect(src, it.Path, e);
            if (eff != DragDropEffects.None) _ = _vm.TransferAsync(src, it.Path, eff == DragDropEffects.Move);
        }
        e.Handled = true;
    }

    // ================= details header / sorting =================
    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Role: not GridViewColumnHeaderRole.Padding } h || h.Column == null) return;
        var idx = ((GridView)DetailsList.View).Columns.IndexOf(h.Column);
        if (idx >= 0) _vm.SetSort(ColumnSorts[idx]);
    }

    private void UpdateHeaders()
    {
        if (DetailsList.View is not GridView gv) return;
        for (int i = 0; i < gv.Columns.Count && i < ColumnNames.Length; i++)
        {
            var sorted = ColumnSorts[i] == _vm.Settings.SortColumn;
            gv.Columns[i].Header = ColumnNames[i] + (sorted ? (_vm.Settings.SortDescending ? "  ▼" : "  ▲") : "");
        }
    }

    // ================= selection =================
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender != ActiveList) return;
        _vm.CurrentTab?.SetSelection(((ListBox)sender).SelectedItems);
        _vm.UpdatePreview();
    }

    private void OnItemsRebuilt(TabViewModel tab)
    {
        if (tab != _vm.CurrentTab) return;
        var pending = tab.PendingSelect;
        if (pending != null)
        {
            tab.ClearPendingSelect();
            var list = ActiveList;
            var items = tab.Items.Where(i => pending.Any(p => Paths.Same(p, i.FullPath))).ToList();
            list.SelectedItems.Clear();
            foreach (var i in items) list.SelectedItems.Add(i);
            if (items.Count > 0) list.ScrollIntoView(items[0]);
        }
        if (!(Keyboard.FocusedElement is TextBox) && AddressBox.Visibility != Visibility.Visible) FocusList();
        ScrollCrumbs();
    }

    // ================= list mouse =================
    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static ItemViewModel? ItemAt(ListBox lb, object? source)
    {
        try { return source is DependencyObject d ? (ItemsControl.ContainerFromElement(lb, d) as ListBoxItem)?.DataContext as ItemViewModel : null; }
        catch { return null; }
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lb = (ListBox)sender;
        _dragReady = false; _pendingClick = null; _dragItem = null;
        if (e.OriginalSource is DependencyObject src && FindAncestor<TextBox>(src) != null) return;
        if (e.OriginalSource is DependencyObject s2 && (FindAncestor<ScrollBar>(s2) != null || FindAncestor<GridViewColumnHeader>(s2) != null)) return;
        var it = ItemAt(lb, e.OriginalSource);
        if (it == null)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) lb.UnselectAll();
            lb.Focus();
            return;
        }
        if (e.ClickCount == 2)
        {
            if (!lb.SelectedItems.Contains(it)) { lb.SelectedItems.Clear(); lb.SelectedItems.Add(it); }
            _vm.Open(it, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(lb);
        _dragItem = it;
        _dragReady = true;
        if (Keyboard.Modifiers == ModifierKeys.None && lb.SelectedItems.Count > 1 && lb.SelectedItems.Contains(it))
        {
            _pendingClick = it; // keep the multi-selection so it can be dragged; collapse on mouse-up
            lb.Focus();
            e.Handled = true;
        }
        _anchor = lb.Items.IndexOf(it);
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var lb = (ListBox)sender;
        if (_pendingClick != null)
        {
            var it = _pendingClick;
            lb.SelectedItems.Clear(); lb.SelectedItems.Add(it);
        }
        _pendingClick = null; _dragReady = false;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragReady || e.LeftButton != MouseButtonState.Pressed) return;
        var lb = (ListBox)sender;
        var p = e.GetPosition(lb);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragReady = false; _pendingClick = null;
        var sel = lb.SelectedItems.Cast<ItemViewModel>().ToList();
        if (_dragItem != null && !sel.Contains(_dragItem)) sel = new List<ItemViewModel> { _dragItem };
        if (sel.Count == 0) return;
        var data = new DataObject(DataFormats.FileDrop, sel.Select(i => i.FullPath).ToArray());
        try { DragDrop.DoDragDrop(lb, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (Exception ex) { _vm.Notify("Drag failed: " + ex.Message); }
        SetDropHover(null);
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var it = ItemAt((ListBox)sender, e.OriginalSource);
        if (it is { IsDirectory: true }) { _vm.OpenInNewTab(it.FullPath); e.Handled = true; }
    }

    private Point _menuPoint;

    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lb = (ListBox)sender;
        if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) != null) return;
        var it = ItemAt(lb, e.OriginalSource);
        if (it == null) lb.UnselectAll();
        else if (!lb.SelectedItems.Contains(it)) { lb.SelectedItems.Clear(); lb.SelectedItems.Add(it); }
        lb.Focus();
        e.Handled = true;
    }

    private void List_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var lb = (ListBox)sender;
        if (e.OriginalSource is DependencyObject d && (FindAncestor<TextBox>(d) != null || FindAncestor<GridViewColumnHeader>(d) != null)) return;
        _menuPoint = e.GetPosition(lb);
        var it = ItemAt(lb, e.OriginalSource);
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { ShowNative(lb); return; }
        ShowFrostMenu(lb, it != null);
    }

    private void ShowNative(ListBox lb)
    {
        var t = _vm.CurrentTab;
        if (t == null) return;
        var sel = t.Selection;
        try
        {
            var pt = lb.PointToScreen(_menuPoint); // physical pixels
            if (sel.Count == 0) ShellActions.ShowNativeContextMenu(this, t.Path, Array.Empty<string>(), pt);
            else
            {
                var folder = System.IO.Path.GetDirectoryName(sel[0].FullPath) ?? t.Path;
                var paths = sel.Select(s => s.FullPath).Where(p => Paths.Same(System.IO.Path.GetDirectoryName(p), folder)).ToList();
                ShellActions.ShowNativeContextMenu(this, folder, paths, pt);
            }
        }
        catch (Exception ex) { _vm.Notify("Couldn't show the Windows menu: " + ex.Message); }
    }

    private void ShowFrostMenu(ListBox lb, bool onItem)
    {
        var t = _vm.CurrentTab;
        if (t == null) return;
        var m = new ContextMenu();
        var sel = t.Selection;
        if (onItem && sel.Count > 0)
        {
            var one = sel.Count == 1 ? sel[0] : null;
            m.Items.Add(MI("Open", "", _vm.OpenSelection, "Enter"));
            if (one is { IsDirectory: true }) m.Items.Add(MI("Open in new tab", "", () => _vm.OpenInNewTab(one.FullPath)));
            if (one is { IsDirectory: false }) m.Items.Add(MI("Open with…", "", () => _vm.OpenWith(one.FullPath)));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Cut", "", () => _vm.CopySelection(true), "Ctrl+X"));
            m.Items.Add(MI("Copy", "", () => _vm.CopySelection(false), "Ctrl+C"));
            if (one is { IsDirectory: true }) m.Items.Add(MI("Paste into folder", "", () => _vm.Paste(one.FullPath), enabled: _vm.CanPaste()));
            m.Items.Add(MI("Copy path", "", _vm.CopyPath, "Ctrl+Shift+C"));
            m.Items.Add(MI(one != null ? "Rename" : "Rename…", "", RenameSelection, "F2"));
            m.Items.Add(MI("Delete", "", () => _vm.Delete(false), "Del"));
            m.Items.Add(new Separator());
            if (one is { IsDirectory: true }) m.Items.Add(MI(_vm.IsPinned(one.FullPath) ? "Unpin from sidebar" : "Pin to sidebar", "", () => _vm.TogglePin(one.FullPath)));
            m.Items.Add(MI("Open in terminal", "", () => _vm.OpenTerminal(one is { IsDirectory: true } ? one.FullPath : t.Path)));
            m.Items.Add(MI("Properties", "", () => _vm.Properties(), "Alt+Enter"));
        }
        else
        {
            var view = new MenuItem { Header = "View", Icon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 14 } };
            foreach (var i in BuildViewMenu().Items.Cast<object>().ToList()) { ((ContextMenu)((MenuItem)i).Parent).Items.Remove(i); view.Items.Add(i); }
            var sort = new MenuItem { Header = "Sort by", Icon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 14 } };
            foreach (var i in BuildSortMenu().Items.Cast<object>().ToList()) { ((ContextMenu)((FrameworkElement)i).Parent).Items.Remove(i); sort.Items.Add(i); }
            m.Items.Add(view);
            m.Items.Add(sort);
            m.Items.Add(new Separator());
            m.Items.Add(MI("New folder", "", _vm.NewFolder, "Ctrl+Shift+N"));
            m.Items.Add(MI("New text document", "", _vm.NewTextFile));
            m.Items.Add(MI("Paste", "", () => _vm.Paste(), "Ctrl+V", enabled: _vm.CanPaste()));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Open in terminal", "", () => _vm.OpenTerminal()));
            m.Items.Add(MI("Refresh", "", t.Refresh, "F5"));
            m.Items.Add(MI("Properties", "", () => _vm.Properties(t.Path)));
        }
        m.Items.Add(new Separator());
        m.Items.Add(MI("Show more options", "", () => ShowNative(lb), "Shift+Right-click"));
        m.PlacementTarget = lb; m.Placement = PlacementMode.MousePoint; m.IsOpen = true;
    }

    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _vm.IsDetails) return;
        _vm.ChangeIconSize(e.Delta > 0 ? 8 : -8);
        e.Handled = true;
    }

    // ================= drag & drop =================
    private static DragDropEffects DecideEffect(string[] src, string dest, DragEventArgs e)
    {
        if (src.Any(s => Paths.Same(s, dest))) return DragDropEffects.None;
        DragDropEffects eff;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) eff = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) eff = DragDropEffects.Move;
        else eff = string.Equals(System.IO.Path.GetPathRoot(src[0]), System.IO.Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase)
                ? DragDropEffects.Move : DragDropEffects.Copy;
        if (eff == DragDropEffects.Move && src.All(s => Paths.Same(System.IO.Path.GetDirectoryName(s), dest))) return DragDropEffects.None;
        return eff;
    }

    private void SetDropHover(ItemViewModel? it)
    {
        if (_dropHover == it) return;
        if (_dropHover != null) _dropHover.IsDropTarget = false;
        _dropHover = it;
        if (it != null) it.IsDropTarget = true;
    }

    private (string? dest, ItemViewModel? hover) DropTargetFor(ListBox lb, DragEventArgs e)
    {
        var it = ItemAt(lb, e.OriginalSource);
        if (it is { IsDirectory: true }) return (it.FullPath, it);
        return (_vm.CurrentTab?.Path, null);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] src || src.Length == 0) { SetDropHover(null); return; }
        var (dest, hover) = DropTargetFor((ListBox)sender, e);
        if (dest == null) { SetDropHover(null); return; }
        e.Effects = DecideEffect(src, dest, e);
        SetDropHover(e.Effects == DragDropEffects.None ? null : hover);
    }

    private void List_DragLeave(object sender, DragEventArgs e) => SetDropHover(null);

    private void List_Drop(object sender, DragEventArgs e)
    {
        SetDropHover(null);
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] src || src.Length == 0) return;
        var (dest, _) = DropTargetFor((ListBox)sender, e);
        if (dest == null) return;
        var eff = DecideEffect(src, dest, e);
        if (eff != DragDropEffects.None) _ = _vm.TransferAsync(src, dest, eff == DragDropEffects.Move);
    }

    // ================= inline rename =================
    private void RenameBox_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true, DataContext: ItemViewModel { IsRenaming: true } it } tb) return;
        tb.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!it.IsRenaming) return;
            tb.Focus();
            tb.SelectionStart = 0;
            tb.SelectionLength = it.IsDirectory ? tb.Text.Length : System.IO.Path.GetFileNameWithoutExtension(tb.Text).Length;
        }));
    }

    private async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ItemViewModel it }) return;
        if (e.Key == Key.Escape) { it.IsRenaming = false; e.Handled = true; FocusList(); }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            try { if (await _vm.CommitRename(it, false)) FocusList(); } catch (Exception ex) { _vm.Notify(ex.Message); }
        }
    }

    private async void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ItemViewModel { IsRenaming: true } it })
        {
            try { await _vm.CommitRename(it, true); } catch (Exception ex) { _vm.Notify(ex.Message); }
        }
    }

    // ================= icon-grid keyboard =================
    private void IconList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt) || e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var lb = IconList;
        int count = lb.Items.Count;
        if (count == 0) return;
        int perRow = Math.Max(1, FindPanel()?.ItemsPerRow ?? 1);
        int cur = lb.SelectedItems.Count == 0 ? -1 : lb.Items.IndexOf(lb.SelectedItems[lb.SelectedItems.Count - 1]);
        if (Keyboard.FocusedElement is ListBoxItem fi && fi.DataContext is ItemViewModel fvm) cur = lb.Items.IndexOf(fvm);
        int next = e.Key switch
        {
            Key.Right => cur + 1, Key.Left => cur - 1, Key.Down => cur + perRow, Key.Up => cur - perRow,
            Key.Home => 0, Key.End => count - 1, Key.PageDown => cur + perRow * 3, Key.PageUp => cur - perRow * 3, _ => -2
        };
        if (next == -2) return;
        if (cur < 0) next = 0;
        next = Math.Clamp(next, 0, count - 1);
        e.Handled = true;
        if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int a = Math.Clamp(_anchor, 0, count - 1);
            lb.SelectedItems.Clear();
            for (int i = Math.Min(a, next); i <= Math.Max(a, next); i++) lb.SelectedItems.Add(lb.Items[i]);
        }
        else { _anchor = next; lb.SelectedItems.Clear(); lb.SelectedItems.Add(lb.Items[next]); }
        lb.ScrollIntoView(lb.Items[next]);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => (lb.ItemContainerGenerator.ContainerFromIndex(next) as ListBoxItem)?.Focus()));
    }

    private VirtualizingWrapPanel? FindPanel() => FindDescendant<VirtualizingWrapPanel>(IconList);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindDescendant<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    // ================= keyboard shortcuts =================
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        bool inText = Keyboard.FocusedElement is TextBox;
        bool onButton = Keyboard.FocusedElement is ButtonBase;
        var t = _vm.CurrentTab;
        bool h = true;

        if (!inText && ctrl && !alt && shift && key == Key.P) ShowCommandPalette();
        else if (!inText && ctrl && !alt && !shift && key == Key.P) ShowGoToFolder();
        else if (ctrl && !alt && !shift && key == Key.T) _vm.NewTab();
        else if (ctrl && !alt && shift && key == Key.T) _vm.ReopenClosedTab();
        else if (ctrl && !alt && !shift && key == Key.W) _vm.CloseTab(t);
        else if (ctrl && !alt && key == Key.Tab) _vm.CycleTab(shift ? -1 : 1);
        else if ((ctrl && !alt && !shift && key == Key.L) || (alt && !ctrl && key == Key.D) || (!ctrl && !alt && !shift && key == Key.F4)) BeginAddressEdit();
        else if (ctrl && !alt && !shift && (key == Key.F || key == Key.E)) { SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (alt && !ctrl && key == Key.P) _vm.PreviewPane = !_vm.PreviewPane;
        else if (alt && !ctrl && key == Key.Left) t?.GoBack();
        else if (alt && !ctrl && key == Key.Right) t?.GoForward();
        else if (alt && !ctrl && key == Key.Up) t?.Up();
        else if (!ctrl && !alt && key == Key.F5 || ctrl && !alt && !shift && key == Key.R) { t?.Refresh(); _vm.LoadDrives(); }
        else if (ctrl && !alt && shift && key == Key.N) _vm.NewFolder();
        else if (ctrl && !alt && !shift && key == Key.H) _vm.ShowHidden = !_vm.ShowHidden;
        else if (ctrl && !alt && shift && key >= Key.D1 && key <= Key.D4) _vm.SetView((ViewMode)(key - Key.D1));
        else if (inText) h = false;
        else if (ctrl && !alt && !shift && key == Key.C) _vm.CopySelection(false);
        else if (ctrl && !alt && !shift && key == Key.X) _vm.CopySelection(true);
        else if (ctrl && !alt && !shift && key == Key.V) _vm.Paste();
        else if (ctrl && !alt && shift && key == Key.C) _vm.CopyPath();
        else if (ctrl && !alt && !shift && key == Key.A) ActiveList.SelectAll();
        else if (!ctrl && !alt && key == Key.Delete) _vm.Delete(shift);
        else if (!ctrl && !alt && !shift && key == Key.F2) RenameSelection();
        else if (alt && !ctrl && key == Key.Enter) _vm.Properties();
        else if (!ctrl && !alt && !shift && key == Key.Enter && !onButton && ActiveList.IsKeyboardFocusWithin) _vm.OpenSelection();
        else if (!ctrl && !alt && !shift && key == Key.Back && !onButton) t?.GoBack();
        else h = false;

        if (h) e.Handled = true;
    }
}
