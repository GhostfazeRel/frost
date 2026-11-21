using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Frost.Core;

namespace Frost.App;

/// <summary>Split view: a second folder list next to the main one. The "active" side (_vm.ActiveTab) gets the keyboard and the file actions.</summary>
public partial class MainWindow
{
    private void ToggleSplit()
    {
        _vm.ToggleSplit();
        SplitColumn.Width = _vm.SplitOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitBorder.Visibility = _vm.SplitOpen ? Visibility.Visible : Visibility.Collapsed;
        UpdateSideVisuals();
        FocusList();
    }

    private void SetActiveSide(bool split)
    {
        if (split && !_vm.SplitOpen) split = false;
        _vm.ActiveIsSplit = split;
    }

    private void SwitchSide()
    {
        if (!_vm.SplitOpen) return;
        SetActiveSide(!_vm.ActiveIsSplit);
        FocusList();
    }

    private void List_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SetActiveSide(sender == SplitList);

    private void UpdateSideVisuals()
    {
        var rim = (System.Windows.Media.Brush)FindResource("RimBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentRimBrush");
        MainListBorder.BorderBrush = _vm.SplitOpen && !_vm.ActiveIsSplit ? accent : rim;
        SplitBorder.BorderBrush = _vm.SplitOpen && _vm.ActiveIsSplit ? accent : rim;
    }

    private void SplitUp_Click(object sender, RoutedEventArgs e) => _vm.SplitTab?.Up();

    /// <summary>F5 (copy) and the "Move to other side" menu item: selection of the active side goes to the folder of the other side.</summary>
    private void CopyToOtherSide(bool move)
    {
        var src = _vm.ActiveTab;
        var dst = _vm.ActiveIsSplit ? _vm.CurrentTab : _vm.SplitTab;
        if (!_vm.SplitOpen || src == null || dst == null) return;
        var paths = src.Selection.Select(i => i.FullPath).ToList();
        if (paths.Count == 0) { _vm.Notify("Select something to " + (move ? "move" : "copy") + " first."); return; }
        if (Paths.Same(src.Path, dst.Path)) { _vm.Notify("Both sides show the same folder."); return; }
        _ = _vm.TransferAsync(paths, dst.Path, move);
    }
}
