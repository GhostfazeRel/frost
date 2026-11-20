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
        var dlg = new BatchRenameWindow(t.Selection.Select(i => i.FullPath).ToList()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try { await t.RefreshAsync(); } catch (Exception ex) { _vm.Notify(ex.Message); }
        _vm.Notify($"Renamed {dlg.Applied.Count} items.");
    }
}
