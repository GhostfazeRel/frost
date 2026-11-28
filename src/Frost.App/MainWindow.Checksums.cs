using System.IO;
using System.Windows;

namespace Frost.App;

/// <summary>Checksums of one file (MD5/SHA-1/SHA-256, copy or compare) and "Clean up empty folders" (to the Recycle Bin, undoable).</summary>
public partial class MainWindow
{
    private async void ShowChecksums()
    {
        var sel = _vm.ActiveTab?.Selection.Select(i => i.FullPath).ToList() ?? new List<string>();
        if (sel.Count != 1 || !File.Exists(sel[0])) return;
        var path = sel[0];
        (string Md5, string Sha1, string Sha256) h = default;
        var err = DialogWindow.RunWithProgress(this, "Checksums", "Reading " + Path.GetFileName(path) + "…", async (p, ct) => h = await Frost.Core.Checksums.ComputeAsync(path, p, ct));
        if (err is OperationCanceledException) return;
        if (err != null) { _vm.Notify("Couldn't read the file: " + err.Message); return; }
        var msg = $"{Path.GetFileName(path)}\n\nSHA-256\n{h.Sha256}\n\nSHA-1\n{h.Sha1}\n\nMD5\n{h.Md5}";
        while (true)
        {
            int r = DialogWindow.Show(this, "Checksums", msg, new[] { "Copy SHA-256", "Compare…", "Close" }, null, out _);
            if (r == 0) { try { Clipboard.SetText(h.Sha256); _vm.Notify("SHA-256 copied"); } catch { } return; }
            if (r != 1) return;
            var pasted = DialogWindow.Prompt(this, "Compare checksum", "Paste the checksum to compare:", "", "Compare");
            if (pasted == null) continue;
            bool ok = new[] { h.Sha256, h.Sha1, h.Md5 }.Any(x => Frost.Core.Checksums.Matches(pasted, x));
            _vm.Notify(ok ? "Checksum matches" : "Checksum does NOT match");
            return;
        }
    }

    private async void CleanEmptyFolders()
    {
        var t = _vm.ActiveTab;
        if (t == null) return;
        List<string> found;
        try { found = await Task.Run(() => Frost.Core.EmptyFolders.Find(t.Path, CancellationToken.None)); }
        catch (Exception ex) { _vm.Notify("Couldn't scan: " + ex.Message); return; }
        if (found.Count == 0) { _vm.Notify("No empty folders in here"); return; }
        var list = string.Join("\n", found.Take(8).Select(p => "  " + Path.GetRelativePath(t.Path, p)));
        if (found.Count > 8) list += $"\n  … and {found.Count - 8} more";
        if (DialogWindow.Show(this, "Clean up empty folders", $"Move {found.Count} empty folder(s) to the Recycle Bin?\n\n{list}", new[] { "Move to Recycle Bin", "Cancel" }, null, out _) != 0) return;
        try
        {
            Frost.Shell.ShellActions.Recycle(found, this);
            _vm.History.RecordRecycle(found.Where(x => !Directory.Exists(x)));
        }
        catch (Exception ex) { _vm.Notify("Clean up failed: " + ex.Message); }
        _ = t.RefreshAsync();
    }
}
