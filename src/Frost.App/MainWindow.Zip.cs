using Frost.Core;

namespace Frost.App;

public partial class MainWindow
{
    private void CompressSelection()
    {
        var t = _vm.CurrentTab;
        if (t == null || t.Selection.Count == 0) return;
        var paths = t.Selection.Select(i => i.FullPath).ToList();
        string zip = Archives.ZipPathFor(paths, t.Path);
        _ = RunArchiveOp("Compressing", Path.GetDirectoryName(zip)!, async (p, ct) => (await Archives.CompressAsync(paths, zip, p, ct), zip));
    }

    private void ExtractZip(ItemViewModel zipItem, bool pickFolder)
    {
        string parent = _vm.CurrentTab?.Path ?? Path.GetDirectoryName(zipItem.FullPath)!;
        if (pickFolder)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to…", InitialDirectory = parent };
            if (dlg.ShowDialog(this) != true) return;
            parent = dlg.FolderName;
        }
        string dest = Archives.ExtractFolderFor(zipItem.FullPath, parent);
        _ = RunArchiveOp("Extracting", parent, async (p, ct) => (0, await Archives.ExtractAsync(zipItem.FullPath, dest, p, ct)));
    }

    // Runs in the operations panel with cancel; reselects the new zip/folder when its parent is open in a tab.
    private async Task RunArchiveOp(string title, string outputParent, Func<IProgress<OperationProgress>, CancellationToken, Task<(int Skipped, string Output)>> work)
    {
        string? output = null;
        var op = new OperationViewModel(title);
        _vm.Operations.Add(op);
        var progress = new Progress<OperationProgress>(op.Report); // created on the UI thread so Report marshals back
        try
        {
            var (skipped, final) = await Task.Run(() => work(progress, op.Cts.Token));
            output = final;
            if (skipped > 0) _vm.Notify($"Done. Skipped {skipped} linked folder(s) or broken link(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _vm.Notify(title + " failed: " + ex.Message); }
        finally
        {
            try // fire-and-forget caller: nothing here may escape unobserved
            {
                _vm.Operations.Remove(op);
                foreach (var t in _vm.Tabs)
                    if (t.Loaded && Paths.Same(t.Path, outputParent)) _ = t.RefreshAsync(output != null && (File.Exists(output) || Directory.Exists(output)) ? output : null);
            }
            catch { }
        }
    }
}
