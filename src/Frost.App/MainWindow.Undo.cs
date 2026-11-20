using System.Diagnostics;
using System.Windows.Controls;
using Frost.Core;
using Frost.Shell;

namespace Frost.App;

public partial class MainWindow
{
    private bool _undoBusy;

    private void AddUndoItems(ContextMenu m)
    {
        var h = _vm.History;
        m.Items.Add(MI(h.CanUndo ? $"Undo {h.UndoLabel}" : "Undo", "", () => _ = UndoAsync(), "Ctrl+Z", enabled: h.CanUndo && !_undoBusy));
        m.Items.Add(MI(h.CanRedo ? $"Redo {h.RedoLabel}" : "Redo", "", () => _ = RedoAsync(), "Ctrl+Y", enabled: h.CanRedo && !_undoBusy));
        m.Items.Add(new Separator());
    }

    private Task UndoAsync() => RunUndo(redo: false);
    private Task RedoAsync() => RunUndo(redo: true);

    private async Task RunUndo(bool redo)
    {
        if (_undoBusy) return;
        _undoBusy = true;
        var op = new OperationViewModel(redo ? "Redoing" : "Undoing");
        _vm.Operations.Add(op);
        try
        {
            Func<IReadOnlyList<string>, bool> recycle = paths => ShellActions.Recycle(paths, this);
            var progress = new Progress<OperationProgress>(op.Report);
            var r = redo ? await _vm.History.RedoAsync(recycle, progress, op.Cts.Token)
                         : await _vm.History.UndoAsync(recycle, progress, op.Cts.Token);
            if (r.CanOpenRecycleBin)
            {
                if (DialogWindow.Show(this, "Can't undo delete", r.Message, new[] { "Open Recycle Bin", "Close" }, null, out _) == 0)
                    try { Process.Start(new ProcessStartInfo("shell:RecycleBinFolder") { UseShellExecute = true }); } catch { }
            }
            else _vm.Notify(r.Message);
        }
        finally
        {
            _vm.Operations.Remove(op);
            _undoBusy = false;
            foreach (var t in _vm.Tabs) if (t.Loaded) _ = t.RefreshAsync();
        }
    }
}
