using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Frost.Core;
using Frost.Core.Tests;
using SkiaSharp;
using Xunit;

namespace Frost.Linux.Tests;

/// <summary>0.4.0: click-to-preview, Quick Look, photo tools, palette, space analyzer, biggest files, split view, duplicates, thumbnails.</summary>
public class Feature040Tests
{
    static void Slice() => Dispatcher.UIThread.MainLoop(new CancellationTokenSource(20).Token);
    static void Pump(int ms = 400) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) Slice(); }
    static bool WaitFor(Func<bool> cond, int ms = 8000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Slice(); if (cond()) return true; } return cond(); }

    static byte[] Pic(int w, int h, SKEncodedImageFormat f)
    {
        using var b = new SKBitmap(w, h);
        using (var c = new SKCanvas(b)) { c.Clear(SKColors.Red); using var p = new SKPaint { Color = SKColors.Blue }; c.DrawRect(0, 0, w / 2f, h, p); }
        using var img = SKImage.FromBitmap(b);
        return img.Encode(f, 90).ToArray();
    }

    /// <summary>A real JPEG (Skia) with a photo EXIF block (camera + GPS) spliced in after SOI.</summary>
    static byte[] PhotoJpeg()
    {
        var j = Pic(64, 48, SKEncodedImageFormat.Jpeg);
        return j.Take(2).Concat(TestImages.App1(TestImages.PhotoTiff(true))).Concat(j.Skip(2)).ToArray();
    }

    static (MainWindow w, ListBox list, string dir) Open()
    {
        var d = Directory.CreateTempSubdirectory("frost-p-").FullName;
        File.WriteAllBytes(Path.Combine(d, "pic.png"), Pic(40, 30, SKEncodedImageFormat.Png));
        File.WriteAllBytes(Path.Combine(d, "photo.jpg"), PhotoJpeg());
        File.WriteAllText(Path.Combine(d, "bad.jpg"), "this is not a jpeg at all");
        File.WriteAllText(Path.Combine(d, "note.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        var w = new MainWindow(d);
        w.Show();
        Pump();
        return (w, w.FindControl<ListBox>("List")!, d);
    }

    static Item ItemNamed(ListBox list, string name) => list.ItemsSource!.Cast<Item>().First(i => i.Name == name);
    static void Select(ListBox list, params string[] names) { list.SelectedItems!.Clear(); foreach (var n in names) list.SelectedItems.Add(ItemNamed(list, n)); Pump(350); }
    static string RowsText(MainWindow w) => string.Join(" | ", w.FindControl<StackPanel>("PrevRows")!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    static Border Pane(MainWindow w) => w.FindControl<Border>("PreviewPane")!;

    static Window Owned(MainWindow w) { Assert.True(WaitFor(() => w.OwnedWindows.Count > 0)); return w.OwnedWindows.Last(); }
    static void RunPalette(MainWindow w, string query)
    {
        w.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "p");
        var pal = Owned(w);
        Pump(200);
        pal.GetVisualDescendants().OfType<TextBox>().First().Text = query;
        Pump(200);
        pal.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Pump(200);
    }
    static void Undo(MainWindow w) { Pump(300); w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"); }

    [AvaloniaFact]
    public void Clicking_a_picture_opens_the_pane_with_image_and_info_and_a_text_file_closes_it()
    {
        var (w, list, _) = Open();
        Assert.False(Pane(w).IsVisible);
        Select(list, "pic.png");
        Assert.True(Pane(w).IsVisible);
        Assert.True(WaitFor(() => w.FindControl<Image>("PrevImage")!.Source != null), "no preview image");
        var rows = RowsText(w);
        Assert.Contains("40 × 30", rows);
        Assert.Contains("PNG", rows);
        Select(list, "note.txt");
        Assert.False(Pane(w).IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Alt_P_pins_the_pane_and_the_setting_can_turn_auto_open_off()
    {
        var (w, list, _) = Open();
        Select(list, "pic.png");
        w.KeyPress(Key.P, RawInputModifiers.Alt, PhysicalKey.P, "p"); // pin
        Select(list, "note.txt");
        Assert.True(Pane(w).IsVisible, "a pinned pane must stay for non-pictures");
        w.KeyPress(Key.P, RawInputModifiers.Alt, PhysicalKey.P, "p"); // unpin
        Pump(200);
        Assert.False(Pane(w).IsVisible);

        RunPalette(w, "Preview pictures on click"); // off
        Select(list, "pic.png");
        Assert.False(Pane(w).IsVisible, "setting off: no auto-open");
        RunPalette(w, "Preview pictures on click"); // back on (settings persist between tests)
        Select(list, "note.txt"); Select(list, "pic.png");
        Assert.True(Pane(w).IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void A_corrupt_jpg_does_not_crash_and_shows_the_icon()
    {
        var (w, list, _) = Open();
        Select(list, "bad.jpg");
        Assert.True(Pane(w).IsVisible);
        Assert.Null(w.FindControl<Image>("PrevImage")!.Source);
        Assert.True(w.FindControl<Avalonia.Controls.Shapes.Path>("PrevGlyph")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Several_pictures_show_a_count_and_the_photo_shows_camera_and_location()
    {
        var (w, list, _) = Open();
        Select(list, "pic.png", "photo.jpg");
        Assert.Contains("2 pictures", w.FindControl<TextBlock>("PrevInfo")!.Text);
        Select(list, "photo.jpg");
        Assert.True(WaitFor(() => w.FindControl<TextBlock>("PrevBadge")!.IsVisible), "no location badge");
        var rows = RowsText(w);
        Assert.Contains("ACME", rows);
        Assert.Contains("52.", rows);
        Assert.Contains("Copy", rows);
        w.Close();
    }

    [AvaloniaFact]
    public void Palette_swatches_appear_and_clicking_one_copies_its_hex()
    {
        var (w, list, _) = Open();
        Select(list, "pic.png");
        var sw = w.FindControl<WrapPanel>("PrevSwatches")!;
        Assert.True(WaitFor(() => sw.Children.Count > 0), "no swatches");
        ((Button)sw.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.StartsWith("Copied #", w.FindControl<TextBlock>("NoticeText")!.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Space_opens_Quick_Look_arrows_flip_and_Esc_closes()
    {
        var (w, list, _) = Open();
        Select(list, "pic.png");
        w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Assert.True(WaitFor(() => w.QuickLook != null), "Quick Look did not open");
        var q = w.QuickLook!;
        Assert.Equal("pic.png", q.Current.Name);
        Pump(300);
        var before = q.Current.Name;
        q.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); // pic.png is the last item
        Pump(200);
        Assert.NotEqual(before, q.Current.Name);
        q.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(before, q.Current.Name);
        q.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(WaitFor(() => w.QuickLook == null), "Esc did not close it");
        w.Close();
    }

    [AvaloniaFact]
    public void Rotate_a_jpeg_and_CtrlZ_restores_identical_bytes()
    {
        var (w, list, d) = Open();
        var path = Path.Combine(d, "photo.jpg");
        var original = File.ReadAllBytes(path);
        Select(list, "photo.jpg");
        RunPalette(w, "Rotate right");
        Assert.True(WaitFor(() => ImageInfo.TryRead(path)?.Orientation == 6), "orientation not rotated");
        Assert.False(File.ReadAllBytes(path).SequenceEqual(original));
        Assert.True(WaitFor(() => w.FindControl<StackPanel>("Ops")!.Children.Count == 0));
        Undo(w);
        Assert.True(WaitFor(() => File.ReadAllBytes(path).SequenceEqual(original)), "undo did not restore the bytes");
        w.Close();
    }

    [AvaloniaFact]
    public void Remove_location_strips_gps_and_the_badge_goes_away()
    {
        var (w, list, d) = Open();
        var path = Path.Combine(d, "photo.jpg");
        Select(list, "photo.jpg");
        Assert.True(WaitFor(() => w.FindControl<TextBlock>("PrevBadge")!.IsVisible));
        RunPalette(w, "Remove location");
        Assert.True(WaitFor(() => ImageInfo.TryRead(path)?.Exif?.HasLocation != true), "location still there");
        Assert.True(WaitFor(() => !w.FindControl<TextBlock>("PrevBadge")!.IsVisible && !RowsText(w).Contains("Location")), "pane still shows a location");
        w.Close();
    }

    [AvaloniaFact]
    public void Convert_and_resize_write_new_files_and_keep_the_original()
    {
        var (w, list, d) = Open();
        var png = Path.Combine(d, "pic.png");
        var before = File.ReadAllBytes(png);
        Select(list, "pic.png");
        RunPalette(w, "Convert to JPG");
        Assert.True(WaitFor(() => File.Exists(Path.Combine(d, "pic.jpg"))), "no jpg");
        Assert.Equal(before, File.ReadAllBytes(png));
        Assert.True(WaitFor(() => list.SelectedItems!.Cast<Item>().Any(i => i.Name == "pic.jpg")), "output not selected");

        Select(list, "pic.png");
        RunPalette(w, "Resize to 50%");
        var small = Path.Combine(d, "pic (50%).png");
        Assert.True(WaitFor(() => File.Exists(small)), "no resized file");
        var info = ImageInfo.TryRead(small)!;
        Assert.Equal((20, 15), (info.Width, info.Height));
        Assert.Equal(before, File.ReadAllBytes(png));
        w.Close();
    }

    [AvaloniaFact]
    public void Space_analyzer_lists_children_biggest_first()
    {
        var (w, _, d) = Open();
        var root = Directory.CreateDirectory(Path.Combine(d, "sub")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "big"));
        File.WriteAllBytes(Path.Combine(root, "big", "x.bin"), new byte[200_000]);
        File.WriteAllBytes(Path.Combine(root, "small.bin"), new byte[1_000]);
        w.ShowSpace(root);
        Assert.True(WaitFor(() => w.SpaceList?.ItemCount == 2), "no rows");
        var first = (SizeNode)w.SpaceList!.ItemsSource!.Cast<object>().First();
        Assert.Equal("big", first.Name);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(WaitFor(() => w.SpaceList == null), "Esc did not close the analyzer");
        w.Close();
    }

    [AvaloniaFact]
    public void Biggest_files_window_lists_the_biggest_file_of_a_root()
    {
        var (w, _, d) = Open();
        var root = Directory.CreateDirectory(Path.Combine(d, "sub")).FullName;
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "huge.bin"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(root, "b.bin"), new byte[5000]);
        w.ShowBiggestFiles(new[] { root });
        Assert.True(WaitFor(() => w.BiggestLists.Count == 2 && w.BiggestLists[0].ItemCount == 3 && w.BiggestLists[1].ItemCount == 3), "lists not filled");
        Assert.EndsWith("huge.bin", ((BigFile)w.BiggestLists[0].ItemsSource!.Cast<object>().First()).Path);
        w.BiggestWindow!.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Split_view_toggles_and_F5_copies_to_the_other_side()
    {
        var (w, list, d) = Open();
        w.KeyPress(Key.OemPipe, RawInputModifiers.Control, PhysicalKey.Backslash, "\\");
        Assert.True(w.IsSplit);
        Assert.True(WaitFor(() => w.List2.ItemCount > 0), "second side empty");
        var target = Path.Combine(d, "sub");
        w.SecondTab!.Navigate(target);
        Assert.True(WaitFor(() => w.SecondTab.Path == target && w.List2.ItemCount == 0));
        Select(list, "note.txt");
        list.Focus(); Pump(100);
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Assert.True(WaitFor(() => File.Exists(Path.Combine(target, "note.txt"))), "not copied");
        Assert.True(File.Exists(Path.Combine(d, "note.txt")));
        w.KeyPress(Key.OemPipe, RawInputModifiers.Control, PhysicalKey.Backslash, "\\");
        Assert.False(w.IsSplit);
        w.Close();
    }

    [AvaloniaFact]
    public void Duplicate_finder_groups_identical_files_and_trashes_the_older_copy()
    {
        if (!File.Exists("/usr/bin/gio")) return;
        var (w, _, d) = Open();
        var root = Directory.CreateDirectory(Path.Combine(d, "sub")).FullName;
        var older = Path.Combine(root, "one.txt"); var newer = Path.Combine(root, "two.txt");
        File.WriteAllText(older, "same content here"); File.WriteAllText(newer, "same content here");
        File.WriteAllText(Path.Combine(root, "other.txt"), "different");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-5));
        w.ShowDuplicates(root);
        var dw = w.DuplicatesWindow!;
        Button B(string text) => dw.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string ?? "").StartsWith(text));
        Pump(200);
        B("Scan").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(WaitFor(() => w.DuplicateChecks.Count == 2), "no duplicate group");
        B("Keep newest").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(100);
        Assert.Single(w.DuplicateChecks, c => c.IsChecked == true);
        B("Move selected").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(WaitFor(() => !File.Exists(older)), "older copy not trashed");
        Assert.True(File.Exists(newer));
        dw.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Thumbnails_use_the_freedesktop_cache()
    {
        Assert.Equal("file:///a%20b/%C3%A7.png", Thumbnails.FileUri("/a b/ç.png"));
        var (w, _, d) = Open();
        var path = Path.Combine(d, "pic.png");
        var t = Thumbnails.GetAsync(path, 128, default);
        Assert.True(WaitFor(() => t.IsCompleted));
        Assert.NotNull(t.Result);
        var cache = Thumbnails.CachePath(path, false);
        Assert.True(File.Exists(cache), "no cache file");
        var text = Thumbnails.ReadText(File.ReadAllBytes(cache));
        Assert.Equal(Thumbnails.FileUri(path), text["Thumb::URI"]);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds().ToString(), text["Thumb::MTime"]);
        w.Close();
    }
}
