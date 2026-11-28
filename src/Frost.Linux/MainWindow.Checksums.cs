using Avalonia.Input.Platform;
using Frost.Core;
namespace Frost.Linux;

// Checksums of one file (MD5/SHA-1/SHA-256, copy or compare) and "Clean up empty folders" (to the trash, undoable).
public partial class MainWindow
{
    async void ShowChecksums()
    {
        if (Sel.Count != 1 || Sel[0].IsDirectory) return;
        var path = Sel[0].FullPath;
        (string Md5, string Sha1, string Sha256) h;
        try { h = await Task.Run(() => Checksums.ComputeAsync(path, null, CancellationToken.None)); }
        catch (Exception ex) { Notify("Couldn't read the file: " + ex.Message); return; }
        var msg = $"{Path.GetFileName(path)}\n\nSHA-256\n{h.Sha256}\n\nSHA-1\n{h.Sha1}\n\nMD5\n{h.Md5}";
        while (true)
        {
            int r = (await Dialogs.Ask(this, "Checksums", msg, new[] { "Copy SHA-256", "Compare…", "Close" })).Index;
            if (r == 0) { _ = Clipboard?.SetTextAsync(h.Sha256); Notify("SHA-256 copied"); return; }
            if (r != 1) return;
            var pasted = await Dialogs.Prompt(this, "Paste the checksum to compare", "", false);
            if (pasted == null) continue;
            bool ok = new[] { h.Sha256, h.Sha1, h.Md5 }.Any(x => Checksums.Matches(pasted, x));
            Notify(ok ? "Checksum matches" : "Checksum does NOT match");
            return;
        }
    }

    async void CleanEmptyFolders()
    {
        var t = ActiveTab;
        if (t == null) return;
        List<string> found;
        try { found = await Task.Run(() => EmptyFolders.Find(t.Path, CancellationToken.None)); }
        catch (Exception ex) { Notify("Couldn't scan: " + ex.Message); return; }
        if (found.Count == 0) { Notify("No empty folders in here"); return; }
        var list = string.Join("\n", found.Take(8).Select(p => "  " + Path.GetRelativePath(t.Path, p)));
        if (found.Count > 8) list += $"\n  … and {found.Count - 8} more";
        if ((await Dialogs.Ask(this, "Clean up empty folders", $"Move {found.Count} empty folder(s) to the trash?\n\n{list}", new[] { "Move to trash", "Cancel" })).Index != 0) return;
        if (await Task.Run(() => Platform.Trash(found))) _history.RecordRecycle(found.Where(x => !Directory.Exists(x)));
        else Notify("Couldn't move to the trash. Nothing was deleted.");
        _ = t.RefreshAsync();
    }
}
