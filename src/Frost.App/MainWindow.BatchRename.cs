using System.Windows;
using Frost.Core;

namespace Frost.App;

public partial class MainWindow
{
    /// <summary>F2 / Rename: inline editor for one item, the batch dialog for several.</summary>
    private async void RenameSelection()
    {
        var t = _vm.CurrentTab;
        if (t is not { Selection.Count: > 1 }) { _vm.BeginRenameSelection(); return; }
        // {n} numbering must follow the list order, not Ctrl-click order.
        var order = new Dictionary<ItemViewModel, int>();
        foreach (var it in t.Items) order[it] = order.Count;
        var paths = t.Selection.OrderBy(i => order.GetValueOrDefault(i, int.MaxValue)).Select(i => i.FullPath).ToList();
        var dlg = new BatchRenameWindow(paths) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Applied.Count == 0) return;
        try { await t.RefreshAsync(); } catch (Exception ex) { _vm.Notify(ex.Message); }
        _vm.Notify($"Renamed {dlg.Applied.Count} items.");
    }
}
