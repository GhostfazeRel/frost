using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Frost.Core;
namespace Frost.Linux;

// Split view (Ctrl+\): a second folder list beside the first with its own folder/navigation. F6 switches side,
// F5 copies to the other side. The side with focus is the "active" one: Sel, Paste, Delete etc. act on it.
public partial class MainWindow
{
    bool _split, _active2;
    Tab? _tab2;
    internal readonly ListBox List2 = new() { Background = Brushes.Transparent, SelectionMode = SelectionMode.Multiple };
    readonly TextBlock _side2Path = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) };
    internal bool IsSplit => _split;
    internal Tab? SecondTab => _tab2;

    bool Side2Active => _split && _tab2 != null && _active2;
    Tab? ActiveTab => Side2Active ? _tab2 : _cur;
    IEnumerable<Tab> AllTabs => _tab2 == null ? _tabs : _tabs.Append(_tab2);

    void SetUpSplit()
    {
        ListArea.ColumnDefinitions = new("*,0,0");
        var up = new Button { Content = "↑", Padding = new Thickness(10, 2) };
        ToolTip.SetTip(up, "Up (Backspace)");
        Avalonia.Automation.AutomationProperties.SetName(up, "Up one level, right side");
        up.Click += (_, _) => _tab2?.Up();
        var bar = new DockPanel { Margin = new Thickness(2, 0, 2, 4) };
        DockPanel.SetDock(up, Dock.Left);
        bar.Children.Add(up); bar.Children.Add(_side2Path);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar); dock.Children.Add(List2);
        Side2Host.Child = dock;

        List2.SelectionChanged += (_, _) => { if (_tab2 != null) { _tab2.Selection = List2.SelectedItems!.Cast<Item>().ToList(); if (Side2Active) UpdatePreview(); } };
        List2.DoubleTapped += (_, e) => { if (ItemAt(e.Source) is { } it) { if (it.IsDirectory) _tab2?.Navigate(it.FullPath); else OpenPath(it.FullPath); } };
        List2.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            _active2 = true;
            var p = e.GetCurrentPoint(List2).Properties;
            var it = ItemAt(e.Source);
            if (p.IsRightButtonPressed)
            {
                if (it == null) List2.SelectedItems!.Clear();
                else if (!List2.SelectedItems!.Contains(it)) { List2.SelectedItems.Clear(); List2.SelectedItems.Add(it); }
                e.Handled = true;
            }
            else if (p.IsLeftButtonPressed && it == null) { List2.Focus(); if (e.KeyModifiers == KeyModifiers.None) List2.SelectedItems!.Clear(); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        List2.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            e.Handled = true; _active2 = true;
            Flyout(List2, ContextMenuItems(ItemAt(e.Source) != null), atPointer: true);
        };
        List.AddHandler(GotFocusEvent, (_, _) => SwitchActive(false), RoutingStrategies.Bubble, handledEventsToo: true);
        List2.AddHandler(GotFocusEvent, (_, _) => SwitchActive(true), RoutingStrategies.Bubble, handledEventsToo: true);
        WireDrag(List2, () => _tab2);
    }

    void SwitchActive(bool second)
    {
        if (!_split || _active2 == second) return;
        _active2 = second;
        UpdatePreview();
    }

    void ToggleSplit()
    {
        if (_split)
        {
            _split = false; _active2 = false;
            Side2Host.IsVisible = false; SplitSplitter.IsVisible = false;
            ListArea.ColumnDefinitions = new("*,0,0");
            List2.ItemsSource = null;
            _tab2?.Dispose(); _tab2 = null;
            FocusList(); UpdatePreview();
            return;
        }
        if (_cur == null) return;
        var t = _tab2 = new Tab(_s, Notify, _cur.Path);
        t.Rebuilt += tab =>
        {
            var pending = tab.TakePendingSelect();
            List2.ItemsSource = tab.Items;
            if (pending != null)
            {
                var items = tab.Items.Where(i => pending.Any(p => Platform.Same(p, i.FullPath))).ToList();
                foreach (var i in items) List2.SelectedItems!.Add(i);
                if (items.Count > 0) List2.ScrollIntoView(items[0]);
            }
        };
        t.Navigated += tab => _side2Path.Text = tab.Path;
        _side2Path.Text = t.Path;
        _split = true;
        ListArea.ColumnDefinitions = new("*,Auto,*");
        Side2Host.IsVisible = true; SplitSplitter.IsVisible = true;
        t.EnsureLoaded();
        FocusList();
    }

    void FocusSide2()
    {
        var i = List2.SelectedIndex >= 0 ? List2.SelectedIndex : 0;
        if (List2.ContainerFromIndex(i) is not Control c || !c.Focus()) List2.Focus();
        _active2 = true;
    }

    void SwitchSide() { if (!_split) return; if (Side2Active) { _active2 = false; FocusList(); } else FocusSide2(); UpdatePreview(); }

    void ToOtherSide(bool move)
    {
        if (!_split || _tab2 == null || _cur == null) return;
        var (from, to) = Side2Active ? (_tab2, _cur) : (_cur, _tab2);
        if (to.SearchQuery != null) { Notify("The other side is showing search results."); return; }
        var paths = from.Selection.Select(i => i.FullPath).ToList();
        if (paths.Count == 0) { Notify("Select something first."); return; }
        _ = TransferAsync(paths, to.Path, move);
    }

    /// <summary>Keys that must beat the ordinary shortcuts: split view, Quick Look, the space view, and the second side's navigation.</summary>
    bool HookKeys(KeyEventArgs e)
    {
        var key = e.Key;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        bool inText = focus is TextBox, onButton = focus is Button;
        if (ctrl && !alt && !shift && (key == Key.OemPipe || key == Key.OemBackslash)) { ToggleSplit(); return true; }
        if (inText) return false;
        if (_space != null && !ctrl && !alt && _spaceKey != null && _spaceKey(key)) return true;
        if (!ctrl && !alt && !shift && key == Key.Space && !onButton) { ShowQuickLook(); return true; }
        if (!_split) return false;
        if (!ctrl && !alt && !shift && key == Key.F6) { SwitchSide(); return true; }
        if (!ctrl && !alt && !shift && key == Key.F5) { ToOtherSide(false); return true; }
        if (!Side2Active) return false;
        var t = _tab2!;
        if (!ctrl && !alt && !shift && key == Key.Enter && !onButton) { if (t.Selection.Count == 1 && t.Selection[0].IsDirectory) t.Navigate(t.Selection[0].FullPath); else foreach (var i in t.Selection.ToList()) if (!i.IsDirectory) OpenPath(i.FullPath); return true; }
        if ((!ctrl && !alt && !shift && key == Key.Back && !onButton) || (alt && !ctrl && key == Key.Up)) { t.Up(); return true; }
        if (alt && !ctrl && key == Key.Left) { t.GoBack(); return true; }
        if (alt && !ctrl && key == Key.Right) { t.GoForward(); return true; }
        if (ctrl && !alt && !shift && key == Key.A) { List2.SelectAll(); return true; }
        return false;
    }
}
