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
        _ = RunArchiveOp("Compressing", zip, Path.GetDirectoryName(zip)!, (p, ct) => Archives.CompressAsync(paths, zip, p, ct));
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
        _ = RunArchiveOp("Extracting", dest, parent, async (p, ct) => { dest = await Archives.ExtractAsync(zipItem.FullPath, dest, p, ct); return 0; });
    }

    // Runs in the operations panel with cancel; reselects the new zip/folder when its parent is open in a tab.
    private async Task RunArchiveOp(string title, string output, string outputParent, Func<IProgress<OperationProgress>, CancellationToken, Task<int>> work)
    {
        var op = new OperationViewModel(title);
        _vm.Operations.Add(op);
        var progress = new Progress<OperationProgress>(op.Report); // created on the UI thread so Report marshals back
        try
        {
            int skipped = await Task.Run(() => work(progress, op.Cts.Token));
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
                    if (t.Loaded && Paths.Same(t.Path, outputParent)) _ = t.RefreshAsync(File.Exists(output) || Directory.Exists(output) ? output : null);
            }
            catch { }
        }
    }
}
