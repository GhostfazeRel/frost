using Avalonia.Controls;
using Frost.Core;
using SkiaSharp;
namespace Frost.Linux;

// Rotate, convert, resize, remove location. In-place edits (JPEG EXIF only) keep a backup and are undoable; conversions write new files.
public partial class MainWindow
{
    record PhotoResult(string Path, string? Output = null, string? Backup = null, string? Error = null);

    List<string> SelectedPictures() => Sel.Where(IsPicture).Select(i => i.FullPath).ToList();

    IEnumerable<Control> PhotoMenuItems()
    {
        if (Sel.Count == 0 || !Sel.All(IsPicture)) yield break;
        yield return new Separator();
        yield return MI("Rotate left", () => RotatePictures(false));
        yield return MI("Rotate right", () => RotatePictures(true));
        yield return new MenuItem { Header = "Convert to", ItemsSource = new Control[] { MI("PNG", () => ConvertPictures(SKEncodedImageFormat.Png)), MI("JPG", () => ConvertPictures(SKEncodedImageFormat.Jpeg)), MI("WebP", () => ConvertPictures(SKEncodedImageFormat.Webp)) } };
        yield return new MenuItem { Header = "Resize", ItemsSource = new Control[] { MI("50%", () => ResizePictures(0.5, 0, "50%")), MI("1920 px", () => ResizePictures(0, 1920, "1920px")), MI("1280 px", () => ResizePictures(0, 1280, "1280px")), MI("Custom…", ResizeCustom) } };
        yield return MI("Remove location", RemoveLocation);
    }

    List<Cmd> PhotoCommands()
    {
        var cmds = new List<Cmd>
        {
            new("What's using space?", "", () => ShowSpace()),
            new("Find biggest files", "", () => ShowBiggestFiles()),
            new("Find duplicate files", "", () => ShowDuplicates()),
            new("Clean up empty folders", "", CleanEmptyFolders),
            new("Checksums of selected file", "", ShowChecksums),
            new(_split ? "Close split view" : "Split view", "Ctrl+\\", ToggleSplit),
            new("Quick Look", "Space", ShowQuickLook),
            new(_s.PreviewOnClick ? "Don't preview pictures on click" : "Preview pictures on click", "", TogglePreviewOnClick),
        };
        if (Sel.Count > 0 && Sel.All(IsPicture))
        {
            cmds.Add(new("Rotate left", "", () => RotatePictures(false)));
            cmds.Add(new("Rotate right", "", () => RotatePictures(true)));
            cmds.Add(new("Convert to PNG", "", () => ConvertPictures(SKEncodedImageFormat.Png)));
            cmds.Add(new("Convert to JPG", "", () => ConvertPictures(SKEncodedImageFormat.Jpeg)));
            cmds.Add(new("Convert to WebP", "", () => ConvertPictures(SKEncodedImageFormat.Webp)));
            cmds.Add(new("Resize to 50%", "", () => ResizePictures(0.5, 0, "50%")));
            cmds.Add(new("Resize to 1920 px", "", () => ResizePictures(0, 1920, "1920px")));
            cmds.Add(new("Resize to 1280 px", "", () => ResizePictures(0, 1280, "1280px")));
            cmds.Add(new("Resize to custom size", "", ResizeCustom));
            cmds.Add(new("Remove location", "", RemoveLocation));
        }
        return cmds;
    }

    void RotatePictures(bool clockwise) => _ = RunPhotoJob(clockwise ? "Rotating right" : "Rotating left", clockwise ? "rotate right" : "rotate left", SelectedPictures(), p =>
    {
        var info = ImageInfo.TryRead(p);
        if (info?.Format != ImageFormat.Jpeg) return new(p, Error: "only JPEG photos can be rotated without re-encoding");
        var backup = EditBackups.Create(p);
        var err = ExifEdit.SetOrientation(p, ExifEdit.Rotate(info.Orientation, clockwise));
        return err == null ? new(p, Backup: backup) : new(p, Error: err);
    });

    void RemoveLocation() => _ = RunPhotoJob("Removing location", "remove location", SelectedPictures(), p =>
    {
        var info = ImageInfo.TryRead(p);
        if (info?.Format != ImageFormat.Jpeg) return new(p, Error: "only JPEG photos carry a location Frost can remove");
        if (info.Exif is not { HasLocation: true }) return new(p, Error: "no location in it");
        var backup = EditBackups.Create(p);
        var err = ExifEdit.RemoveLocation(p);
        return err == null ? new(p, Backup: backup) : new(p, Error: err);
    });

    void ConvertPictures(SKEncodedImageFormat format) => _ = RunPhotoJob("Converting to " + ImageOps.ExtFor(format)[1..].ToUpperInvariant(), "", SelectedPictures(), p => Reencode(p, format, 1, 0, ""));

    async void ResizeCustom()
    {
        var text = await Dialogs.Prompt(this, "Resize: longest side in pixels", "1600", true);
        if (text == null) return;
        if (!int.TryParse(text.Trim(), out var px) || px is < 16 or > 30000) { Notify("Enter a size between 16 and 30000 pixels."); return; }
        ResizePictures(0, px, px + "px");
    }

    void ResizePictures(double scale, int longest, string suffix) => _ = RunPhotoJob("Resizing to " + suffix, "", SelectedPictures(), p => Reencode(p, null, scale, longest, " (" + suffix + ")"));

    /// <summary>Writes a new picture next to the original; format null = keep it (PNG when Skia can't encode the original's).</summary>
    static PhotoResult Reencode(string path, SKEncodedImageFormat? format, double scale, int longest, string suffix)
    {
        var info = ImageInfo.TryRead(path);
        if (!ImageOps.SkiaFormat(info) || !ImageOps.Decodable(info, new FileInfo(path).Length)) return new(path, Error: "can't read this format");
        var fmt = format ?? info!.Format switch { ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg, ImageFormat.WebP => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Png };
        using var bmp = ImageOps.Load(path, info!.Orientation);
        if (bmp == null) return new(path, Error: "can't decode it");
        int w = bmp.Width, h = bmp.Height;
        if (scale != 1 || longest > 0)
        {
            double f = longest > 0 ? longest / (double)Math.Max(w, h) : scale;
            if (f >= 1) return new(path, Error: "already smaller than that");
            (w, h) = ((int)Math.Max(1, Math.Round(w * f)), (int)Math.Max(1, Math.Round(h * f)));
        }
        using var scaled = w == bmp.Width ? null : ImageOps.Scale(bmp, w, h);
        var flat = fmt == SKEncodedImageFormat.Jpeg ? ImageOpsExtra.OnWhite(scaled ?? bmp) : scaled ?? bmp;
        var data = ImageOps.Encode(flat, fmt);
        if (flat != bmp && flat != scaled) flat.Dispose();
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path) + suffix;
        string target = ImageOps.UniquePath(dir, stem, ImageOps.ExtFor(fmt)), tmp = target + ".frost-tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, target); // refuses to overwrite
        return new(path, Output: target);
    }

    async Task RunPhotoJob(string title, string editLabel, List<string> paths, Func<string, PhotoResult> work)
    {
        if (paths.Count == 0) return;
        var op = AddOp(title);
        var progress = (IProgress<OperationProgress>)new Progress<OperationProgress>(op.Report);
        var results = new List<PhotoResult>();
        var token = op.Cts.Token;
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new OperationProgress(Path.GetFileName(paths[i]), 0, 0, i, paths.Count));
                    try { results.Add(work(paths[i])); } catch (Exception ex) { results.Add(new(paths[i], Error: ex.Message)); }
                }
            });
        }
        catch (OperationCanceledException) { }
        finally { Ops.Children.Remove(op.Row); }
        foreach (var r in results)
        {
            if (r.Backup != null) _history.RecordEdit(r.Path, r.Backup, editLabel);
            if (r.Output != null) _history.RecordCreate(r.Output, false);
        }
        var outs = results.Select(r => r.Output).OfType<string>().ToList();
        int edited = results.Count(r => r.Backup != null), failed = results.Count(r => r.Error != null);
        var msg = outs.Count > 0 ? $"Created {outs.Count} file(s)." : edited > 0 ? $"{title.Split(' ')[0]} done: {edited} photo(s)." : "Nothing changed.";
        if (failed > 0) msg += $" {failed} skipped: {results.First(r => r.Error != null).Error}.";
        Notify(msg);
        foreach (var t in AllTabs.Where(t => t.Loaded))
        {
            var mine = t == _cur ? outs.Where(o => Platform.Same(Path.GetDirectoryName(o), t.Path)).ToList() : new();
            _ = t.RefreshAsync(null, mine.Count > 0 ? mine : null);
        }
        UpdatePreview();
    }
}
