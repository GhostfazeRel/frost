using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frost.Core;

namespace Frost.App;

/// <summary>Photo tools: rotate / remove location edit JPEGs in place (EXIF only, undoable); convert and resize write new files next to the originals.</summary>
public partial class MainWindow
{
    private sealed record PhotoResult(string Source, string? Created = null, string? Backup = null, string? Label = null, string? Error = null, bool Skipped = false);

    private static bool IsJpeg(ItemViewModel i) => i.Entry.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || i.Entry.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ItemViewModel> SelectedPictures()
    {
        var sel = _vm.ActiveTab?.Selection ?? Array.Empty<ItemViewModel>();
        return sel.Count > 0 && sel.All(PictureLoader.IsPicture) ? sel : Array.Empty<ItemViewModel>();
    }

    // ---- menus ----
    private void AddPhotoItems(ContextMenu m, IReadOnlyList<ItemViewModel> sel)
    {
        if (sel.Count == 0 || !sel.All(PictureLoader.IsPicture)) return;
        bool jpeg = sel.Any(IsJpeg);
        if (jpeg)
        {
            m.Items.Add(MI("Rotate left", null, () => RotatePhotos(false)));
            m.Items.Add(MI("Rotate right", null, () => RotatePhotos(true)));
        }
        var convert = new MenuItem { Header = "Convert to" };
        convert.Items.Add(MI("PNG", null, () => ConvertPhotos("png")));
        convert.Items.Add(MI("JPG", null, () => ConvertPhotos("jpg")));
        m.Items.Add(convert);
        var resize = new MenuItem { Header = "Resize" };
        resize.Items.Add(MI("50%", null, () => ResizePhotos(true, 50)));
        resize.Items.Add(MI("1920 px", null, () => ResizePhotos(false, 1920)));
        resize.Items.Add(MI("1280 px", null, () => ResizePhotos(false, 1280)));
        resize.Items.Add(MI("Custom…", null, ResizePhotosCustom));
        m.Items.Add(resize);
        if (jpeg) m.Items.Add(MI("Remove location", null, RemovePhotoLocation));
        m.Items.Add(new Separator());
    }

    private void AddPhotoCommands(List<PaletteCommand> cmds)
    {
        var sel = SelectedPictures();
        if (sel.Count == 0) return;
        if (sel.Any(IsJpeg))
        {
            cmds.Add(new("Rotate left", "", () => RotatePhotos(false)));
            cmds.Add(new("Rotate right", "", () => RotatePhotos(true)));
            cmds.Add(new("Remove location", "", RemovePhotoLocation));
        }
        cmds.Add(new("Convert to PNG", "", () => ConvertPhotos("png")));
        cmds.Add(new("Convert to JPG", "", () => ConvertPhotos("jpg")));
        cmds.Add(new("Resize to 50%", "", () => ResizePhotos(true, 50)));
        cmds.Add(new("Resize to 1920 px", "", () => ResizePhotos(false, 1920)));
        cmds.Add(new("Resize to 1280 px", "", () => ResizePhotos(false, 1280)));
        cmds.Add(new("Resize to custom size…", "", ResizePhotosCustom));
    }

    // ---- in-place edits (JPEG, EXIF only) ----
    private void RotatePhotos(bool clockwise)
    {
        var files = SelectedPictures().Where(IsJpeg).Select(i => i.FullPath).ToList();
        if (files.Count == 0) return;
        _ = RunPhotoOp(clockwise ? "Rotating right" : "Rotating left", files, (f, ct) =>
        {
            var cur = ImageInfo.TryRead(f)?.Orientation ?? 1;
            var backup = EditBackups.Create(f);
            var err = ExifEdit.SetOrientation(f, ExifEdit.Rotate(cur, clockwise));
            if (err != null) { TryDelete(backup); return new PhotoResult(f, Error: err); }
            return new PhotoResult(f, Backup: backup, Label: clockwise ? "rotate right" : "rotate left");
        }, "Rotated");
    }

    private void RemovePhotoLocation()
    {
        var files = SelectedPictures().Where(IsJpeg).Select(i => i.FullPath).ToList();
        if (files.Count == 0) return;
        _ = RunPhotoOp("Removing location", files, (f, ct) =>
        {
            if (ImageInfo.TryRead(f)?.Exif?.HasLocation != true) return new PhotoResult(f, Skipped: true);
            var backup = EditBackups.Create(f);
            var err = ExifEdit.RemoveLocation(f);
            if (err != null) { TryDelete(backup); return new PhotoResult(f, Error: err); }
            return new PhotoResult(f, Backup: backup, Label: "remove location");
        }, "Removed location from");
    }

    // ---- new files ----
    private void ConvertPhotos(string kind)
    {
        var files = SelectedPictures().Select(i => i.FullPath).ToList();
        if (files.Count == 0) return;
        _ = RunPhotoOp("Converting to " + kind.ToUpperInvariant(), files, (f, ct) =>
        {
            if (KindOf(f) == kind) return new PhotoResult(f, Skipped: true); // already that format
            var info = ImageInfo.TryRead(f);
            if (PictureLoader.TooBig(new FileInfo(f).Length, info)) return new PhotoResult(f, Error: "picture is too large");
            var bmp = PictureLoader.Decode(f, info, 0, ct);
            var dir = Path.GetDirectoryName(f)!;
            var name = PathUtil.UniqueName(dir, Path.GetFileNameWithoutExtension(f) + "." + kind);
            var outPath = Path.Combine(dir, name);
            Encode(bmp, outPath, kind, info?.HasAlpha == true);
            return new PhotoResult(f, Created: outPath);
        }, "Converted");
    }

    private void ResizePhotos(bool percent, double value)
    {
        var files = SelectedPictures().Select(i => i.FullPath).ToList();
        if (files.Count == 0) return;
        string label = percent ? $"{value:0.##}%" : $"{value:0}px";
        _ = RunPhotoOp("Resizing to " + label, files, (f, ct) =>
        {
            var info = ImageInfo.TryRead(f);
            if (info == null) return new PhotoResult(f, Error: "can't read this picture");
            if (PictureLoader.TooBig(new FileInfo(f).Length, info)) return new PhotoResult(f, Error: "picture is too large");
            double dw = info.DisplayWidth, dh = info.DisplayHeight;
            double factor = percent ? value / 100.0 : Math.Min(1.0, value / Math.Max(dw, dh)); // pixels: longest side, never enlarged
            int newW = (int)Math.Max(1, Math.Round(dw * factor));
            BitmapSource bmp = PictureLoader.Decode(f, info, newW, ct);
            if (bmp.PixelWidth != newW) // enlarging, or the decoder didn't shrink exactly
            {
                bmp = new TransformedBitmap(bmp, new ScaleTransform((double)newW / bmp.PixelWidth, (double)newW / bmp.PixelWidth));
                bmp.Freeze();
            }
            var kind = KindOf(f);
            var dir = Path.GetDirectoryName(f)!;
            var name = PathUtil.UniqueName(dir, $"{Path.GetFileNameWithoutExtension(f)} ({label}).{kind}");
            var outPath = Path.Combine(dir, name);
            Encode(bmp, outPath, kind, info.HasAlpha);
            return new PhotoResult(f, Created: outPath);
        }, "Resized");
    }

    private void ResizePhotosCustom()
    {
        if (SelectedPictures().Count == 0) return;
        var text = DialogWindow.Prompt(this, "Resize pictures", "Enter a percentage (like 75%) or the longest side in pixels (like 800).", "1600", "Resize");
        if (text == null) return;
        if (!TryParseSize(text, out var percent, out var value)) { _vm.Notify("Enter a size like 75% or 800."); return; }
        ResizePhotos(percent, value);
    }

    internal static bool TryParseSize(string text, out bool percent, out double value)
    {
        text = text.Trim();
        percent = text.EndsWith('%');
        text = text.TrimEnd('%').Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2].Trim();
        bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return ok && value > 0 && value <= (percent ? 1000 : 20000);
    }

    /// <summary>Output format for a source file: JPEG, PNG, BMP and TIFF keep their format, everything else (GIF, WebP, HEIC...) becomes PNG.</summary>
    private static string KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "jpg", ".bmp" => "bmp", ".tif" or ".tiff" => "tif", _ => "png"
    };

    private static void Encode(BitmapSource bmp, string outPath, string kind, bool hasAlpha)
    {
        BitmapEncoder enc = kind switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "bmp" => new BmpBitmapEncoder(),
            "tif" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        enc.Frames.Add(BitmapFrame.Create(kind == "jpg" && hasAlpha ? FlattenOnWhite(bmp) : bmp));
        try
        {
            using var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write);
            enc.Save(fs);
        }
        catch { TryDelete(outPath); throw; }
    }

    // JPEG has no alpha: blend transparent pixels with white instead of letting them turn black
    private static BitmapSource FlattenOnWhite(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var buf = new byte[w * h * 4];
        conv.CopyPixels(buf, w * 4, 0);
        for (int i = 0; i < buf.Length; i += 4)
        {
            int a = buf[i + 3];
            buf[i] = (byte)((buf[i] * a + 255 * (255 - a)) / 255);
            buf[i + 1] = (byte)((buf[i + 1] * a + 255 * (255 - a)) / 255);
            buf[i + 2] = (byte)((buf[i + 2] * a + 255 * (255 - a)) / 255);
            buf[i + 3] = 255;
        }
        var r = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        r.Freeze();
        return r;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    // ---- runner: operations panel with progress and cancel; one undo entry per file ----
    private async Task RunPhotoOp(string title, IReadOnlyList<string> files, Func<string, CancellationToken, PhotoResult> work, string doneVerb)
    {
        var op = new OperationViewModel(title);
        _vm.Operations.Add(op);
        var progress = new Progress<OperationProgress>(op.Report);
        var results = new List<PhotoResult>();
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < files.Count; i++)
                {
                    op.Cts.Token.ThrowIfCancellationRequested();
                    ((IProgress<OperationProgress>)progress).Report(new OperationProgress(Path.GetFileName(files[i]), 0, 0, i, files.Count));
                    try { results.Add(work(files[i], op.Cts.Token)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { results.Add(new PhotoResult(files[i], Error: ex.Message)); }
                }
            });
        }
        catch (OperationCanceledException) { }
        finally { _vm.Operations.Remove(op); }

        var created = new List<string>();
        foreach (var r in results)
        {
            if (r.Created != null) { created.Add(r.Created); _vm.History.RecordCreate(r.Created, false, redoable: false); }
            else if (r.Backup != null) _vm.History.RecordEdit(r.Source, r.Backup, r.Label ?? "edit");
        }
        int ok = results.Count(r => r.Created != null || r.Backup != null), skipped = results.Count(r => r.Skipped);
        var failed = results.Where(r => r.Error != null).ToList();
        string msg = ok > 0 ? $"{doneVerb} {ok} picture(s)." : "Nothing changed.";
        if (skipped > 0) msg += $" {skipped} skipped (nothing to do).";
        if (failed.Count > 0) msg += $" {failed.Count} failed: {failed[0].Error}";
        _vm.Notify(msg);

        foreach (var t in _vm.AllTabs.ToList())
        {
            if (!t.Loaded) continue;
            var here = created.Where(c => Paths.Same(Path.GetDirectoryName(c), t.Path)).ToList();
            if (here.Count > 0) _ = t.RefreshSelectAsync(here);
            else if (results.Any(r => r.Backup != null && Paths.Same(Path.GetDirectoryName(r.Source), t.Path))) _ = t.RefreshAsync();
        }
        _vm.UpdatePreview();
    }
}
