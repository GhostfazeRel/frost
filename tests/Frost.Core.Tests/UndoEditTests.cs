using Frost.Core;

namespace Frost.Core.Tests;

public sealed class UndoEditTests : IDisposable
{
    private readonly string _d = Directory.CreateTempSubdirectory("frost-undoedit").FullName;
    private readonly string? _oldCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    public UndoEditTests() { if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(_d, "cache")); }
    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _oldCache);
        try { Directory.Delete(_d, true); } catch { }
    }

    private static Task<UndoOutcome> Undo(UndoHistory h, Func<string, bool?>? ask = null) => h.UndoAsync(_ => true, ask ?? (_ => false), null, default);
    private static Task<UndoOutcome> Redo(UndoHistory h) => h.RedoAsync(_ => true, _ => false, null, default);

    [Fact]
    public async Task Rotate_edit_undo_redo_restores_bytes()
    {
        string p = Path.Combine(_d, "photo.jpg");
        var orig = TestImages.Jpeg(40, 30, TestImages.PhotoTiff(true));
        File.WriteAllBytes(p, orig);
        var h = new UndoHistory();
        string backup = EditBackups.Create(p);
        Assert.Null(ExifEdit.SetOrientation(p, 6));
        var edited = File.ReadAllBytes(p);
        h.RecordEdit(p, backup, "rotate");
        await h.FingerprintsReady;
        Assert.Equal("rotate of \"photo.jpg\"", h.UndoLabel);

        var u = await Undo(h);
        Assert.True(u.Ok, u.Message);
        Assert.Equal(orig, File.ReadAllBytes(p));
        Assert.Equal("rotate of \"photo.jpg\"", h.RedoLabel);
        var r = await Redo(h);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(edited, File.ReadAllBytes(p));
        Assert.True((await Undo(h)).Ok); // and back again
        Assert.Equal(orig, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(_d, "*.frost-swap-*"));
    }

    [Fact]
    public async Task Refuses_when_file_changed_since()
    {
        string p = Path.Combine(_d, "a.jpg");
        File.WriteAllBytes(p, TestImages.Jpeg(4, 4, TestImages.PhotoTiff(false)));
        string backup = EditBackups.Create(p);
        ExifEdit.SetOrientation(p, 3);
        var h = new UndoHistory();
        h.RecordEdit(p, backup, "rotate");
        await h.FingerprintsReady;
        File.WriteAllText(p, "someone else wrote this");
        var u = await Undo(h);
        Assert.False(u.Ok);
        Assert.Contains("changed", u.Message);
        Assert.Equal("someone else wrote this", File.ReadAllText(p));
        Assert.True(h.CanUndo); // entry kept
    }

    [Fact]
    public async Task Gone_file_or_backup_drops_entry()
    {
        string p = Path.Combine(_d, "b.jpg");
        File.WriteAllBytes(p, new byte[] { 1 });
        string backup = EditBackups.Create(p);
        var h = new UndoHistory();
        h.RecordEdit(p, backup, "remove location");
        await h.FingerprintsReady;
        File.Delete(backup);
        var u = await Undo(h);
        Assert.False(u.Ok);
        Assert.False(h.CanUndo);
        Assert.Contains("remove location of \"b.jpg\"", u.Message);
    }

    [Fact]
    public void Backups_folder_create_and_cleanup()
    {
        string p = Path.Combine(_d, "c.jpg");
        File.WriteAllText(p, "x");
        string folder = EditBackups.Folder;
        if (!OperatingSystem.IsWindows()) Assert.Equal(Path.Combine(_d, "cache", "frost", "undo"), folder);
        Directory.CreateDirectory(folder);
        string old = Path.Combine(folder, DateTime.UtcNow.AddDays(-8).ToString("yyyyMMddHHmmss") + "-abc-old.jpg");
        string young = Path.Combine(folder, DateTime.UtcNow.AddDays(-2).ToString("yyyyMMddHHmmss") + "-abc-young.jpg");
        File.WriteAllText(old, "o"); File.WriteAllText(young, "y");
        string b1 = EditBackups.Create(p), b2 = EditBackups.Create(p);
        Assert.NotEqual(b1, b2);
        Assert.Equal("x", File.ReadAllText(b1));
        Assert.Equal(folder, Path.GetDirectoryName(b1));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(young));
    }
}
