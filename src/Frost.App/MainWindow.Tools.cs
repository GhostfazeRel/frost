using System.Windows;
using System.Windows.Controls;

namespace Frost.App;

/// <summary>0.4.0 tools: Quick Look, space analyzer, biggest files on every drive, duplicate finder, split view entries.</summary>
public partial class MainWindow
{
    private void AddToolItems(ContextMenu m)
    {
        m.Items.Add(new Separator());
        m.Items.Add(MI(_vm.SplitOpen ? "Close split view" : "Split view", null, ToggleSplit, "Ctrl+\\"));
        m.Items.Add(MI("What's using space?", null, ShowSpace));
        m.Items.Add(MI("Find biggest files", null, ShowBiggestFiles));
        m.Items.Add(MI("Find duplicate files", null, ShowDuplicates));
    }

    private void AddToolCommands(List<PaletteCommand> cmds)
    {
        cmds.Add(new(_vm.SplitOpen ? "Close split view" : "Split view", "Ctrl+\\", ToggleSplit));
        if (_vm.SplitOpen)
        {
            cmds.Add(new("Switch side", "F6", SwitchSide));
            cmds.Add(new("Copy to other side", "F5", () => CopyToOtherSide(false)));
            cmds.Add(new("Move to other side", "", () => CopyToOtherSide(true)));
        }
        cmds.Add(new("Quick Look", "Space", ShowQuickLook));
        cmds.Add(new("What's using space?", "", ShowSpace));
        cmds.Add(new("Find biggest files", "", ShowBiggestFiles));
        cmds.Add(new("Find duplicate files", "", ShowDuplicates));
    }

    // ---- Quick Look ----
    private void ShowQuickLook()
    {
        var t = _vm.ActiveTab;
        var lb = ActiveList;
        if (t == null || t.Selection.Count == 0) return;
        var items = t.Items.ToList();
        int idx = items.IndexOf(t.Selection[0]);
        if (idx < 0) return;
        new QuickLookWindow(this, items, idx, it => _vm.Open(it), it =>
        {
            if (!items.Contains(it) || !t.Items.Contains(it)) return;
            lb.SelectedItems.Clear(); lb.SelectedItems.Add(it); lb.ScrollIntoView(it);
            FocusList();
        }).ShowDialog();
    }

    // ---- space analyzer (an overlay on the file list) ----
    private bool _spaceHooked;

    private void ShowSpace()
    {
        var t = _vm.ActiveTab;
        if (t == null) return;
        if (!_spaceHooked)
        {
            _spaceHooked = true;
            Space.CloseRequested += CloseSpace;
            Space.RevealRequested += path =>
            {
                CloseSpace();
                _vm.ActiveTab?.NavigateSelect(System.IO.Path.GetDirectoryName(path) ?? t.Path, path);
            };
        }
        Space.Visibility = Visibility.Visible;
        Space.Start(_vm, t.Path);
    }

    private void CloseSpace()
    {
        Space.Stop();
        Space.Visibility = Visibility.Collapsed;
        FocusList();
    }

    // ---- windows ----
    private void ShowBiggestFiles()
    {
        new BiggestFilesWindow(this, _vm, path =>
        {
            _vm.NewTab(System.IO.Path.GetDirectoryName(path), select: path);
            Activate();
        }).Show();
    }

    private void ShowDuplicates()
    {
        var folder = _vm.ActiveTab?.Path ?? Paths.Home;
        new DuplicatesWindow(this, _vm, folder).Show();
    }
}
