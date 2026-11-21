using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Frost.Linux.Tests;

/// <summary>Drives the real window: palette, ZIP, batch rename, go to folder, drag and drop.</summary>
public class FeatureTests
{
    // Run the real dispatcher loop (not just RunJobs) so DispatcherTimers fire too.
    static void Slice() => Dispatcher.UIThread.MainLoop(new CancellationTokenSource(20).Token);

    static void Pump(int ms = 500)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) Slice();
    }

    static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Slice(); if (cond()) return true; }
        return cond();
    }

    static (MainWindow w, ListBox list, string dir) Open()
    {
        var d = Directory.CreateTempSubdirectory("frost-f-").FullName;
        File.WriteAllText(Path.Combine(d, "a.txt"), "a");
        File.WriteAllText(Path.Combine(d, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        var w = new MainWindow(d);
        w.Show();
        Pump();
        return (w, w.FindControl<ListBox>("List")!, d);
    }

    static Item ItemNamed(ListBox list, string name) => list.ItemsSource!.Cast<Item>().First(i => i.Name == name);

    static void Select(ListBox list, params string[] names)
    {
        list.SelectedItems!.Clear();
        foreach (var n in names) list.SelectedItems.Add(ItemNamed(list, n));
        Pump(100);
    }

    static string[] Names(string dir) => Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    static Window Owned(MainWindow w) { Assert.True(WaitFor(() => w.OwnedWindows.Count > 0)); return w.OwnedWindows.Last(); }

    /// <summary>Opens a palette with the shortcut, types the query and presses Enter.</summary>
    static void RunPalette(MainWindow w, Key key, RawInputModifiers mods, string query)
    {
        w.KeyPress(key, mods, PhysicalKey.P, "p");
        var pal = Owned(w);
        Pump(200);
        pal.GetVisualDescendants().OfType<TextBox>().First().Text = query;
        Pump(200);
        pal.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Pump(200);
    }

    [AvaloniaFact]
    public void Palette_compresses_to_zip_and_extracts_it_again()
    {
        var (w, list, d) = Open();
        Select(list, "a.txt", "sub");
        RunPalette(w, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, "Compress to ZIP");
        Assert.True(WaitFor(() => Directory.GetFiles(d, "*.zip").Length == 1), "no zip was created");
        var zip = Path.GetFileName(Directory.GetFiles(d, "*.zip")[0]);
        Assert.True(WaitFor(() => list.ItemsSource!.Cast<Item>().Any(i => i.Name == zip)));

        Select(list, zip);
        RunPalette(w, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, "Extract here");
        var extracted = Path.Combine(d, Path.GetFileNameWithoutExtension(zip));
        Assert.True(WaitFor(() => File.Exists(Path.Combine(extracted, "a.txt")) && Directory.Exists(Path.Combine(extracted, "sub"))), "zip was not extracted");
        Assert.Equal("a", File.ReadAllText(Path.Combine(extracted, "a.txt")));
        w.Close();
    }

    [AvaloniaFact]
    public void F2_on_several_items_batch_renames_and_CtrlZ_undoes_it()
    {
        var (w, list, d) = Open();
        Select(list, "a.txt", "b.txt");
        list.Focus();
        w.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        var dlg = Owned(w);
        Pump(200);
        dlg.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "Prefix").Text = "x_";
        var ok = dlg.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Rename");
        Assert.True(WaitFor(() => ok.IsEnabled), "rename never became possible: " + string.Join(" | ", dlg.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
        ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(WaitFor(() => Names(d).SequenceEqual(new[] { "sub", "x_a.txt", "x_b.txt" })), string.Join(",", Names(d)));

        Pump(300);
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.True(WaitFor(() => Names(d).SequenceEqual(new[] { "a.txt", "b.txt", "sub" })), "undo: " + string.Join(",", Names(d)));
        w.Close();
    }

    [AvaloniaFact]
    public void CtrlP_goes_to_a_typed_folder()
    {
        var (w, list, d) = Open();
        File.WriteAllText(Path.Combine(d, "sub", "inner.txt"), "i");
        RunPalette(w, Key.P, RawInputModifiers.Control, Path.Combine(d, "sub"));
        Assert.True(WaitFor(() => list.ItemsSource!.Cast<Item>().Select(i => i.Name).SequenceEqual(new[] { "inner.txt" })), "did not navigate");
        w.Close();
    }

    [AvaloniaFact]
    public void CtrlP_works_from_the_search_box()
    {
        var (w, _, _) = Open();
        var search = w.FindControl<TextBox>("SearchBox")!;
        search.Focus();
        search.Text = "zzz";
        Pump(200);
        w.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");
        Assert.True(WaitFor(() => w.OwnedWindows.Count > 0), "Go to folder did not open from the search box");
        foreach (var o in w.OwnedWindows.ToList()) o.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void Clicking_empty_space_clears_the_selection_and_focuses_the_list()
    {
        var (w, list, _) = Open();
        Select(list, "a.txt", "b.txt");
        w.FindControl<TextBox>("SearchBox")!.Focus();
        // Bottom of the list, well below the three rows.
        var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 10), w)!.Value;
        w.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        Pump(200);
        Assert.Empty(list.SelectedItems!);
        Assert.True(list.IsKeyboardFocusWithin, $"focus stayed in the search box (focusable={list.Focusable}, focused={TopLevel.GetTopLevel(w)!.FocusManager!.GetFocusedElement()?.GetType().Name}, active={w.IsActive})");
        w.Close();
    }

    static void Drop(ListBox list, Interactive target, string[] paths, KeyModifiers mods, DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(MainWindow.DragFormat, string.Join('\n', paths)));
        target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, target, new Point(1, 1), mods) { DragEffects = allowed });
        Pump(300);
    }

    [AvaloniaFact]
    public void Dropping_on_a_folder_moves_and_Ctrl_drop_copies()
    {
        var (w, list, d) = Open();
        var sub = (Interactive)list.ContainerFromItem(ItemNamed(list, "sub"))!;
        Drop(list, sub, new[] { Path.Combine(d, "a.txt") }, KeyModifiers.None);
        Assert.True(WaitFor(() => File.Exists(Path.Combine(d, "sub", "a.txt")) && !File.Exists(Path.Combine(d, "a.txt"))), "plain drop should move on the same drive");

        sub = (Interactive)list.ContainerFromItem(ItemNamed(list, "sub"))!;
        Drop(list, sub, new[] { Path.Combine(d, "b.txt") }, KeyModifiers.Control);
        Assert.True(WaitFor(() => File.Exists(Path.Combine(d, "sub", "b.txt")) && File.Exists(Path.Combine(d, "b.txt"))), "Ctrl drop should copy");

        // A source that only offers a copy (most other apps) keeps its file, even on the same drive.
        File.WriteAllText(Path.Combine(d, "c.txt"), "c");
        sub = (Interactive)list.ContainerFromItem(ItemNamed(list, "sub"))!;
        Drop(list, sub, new[] { Path.Combine(d, "c.txt") }, KeyModifiers.None, DragDropEffects.Copy);
        Assert.True(WaitFor(() => File.Exists(Path.Combine(d, "sub", "c.txt")) && File.Exists(Path.Combine(d, "c.txt"))), "copy-only source must not be moved");

        // A folder dropped onto itself is refused, nothing changes.
        sub = (Interactive)list.ContainerFromItem(ItemNamed(list, "sub"))!;
        Drop(list, sub, new[] { Path.Combine(d, "sub") }, KeyModifiers.None);
        Assert.True(Directory.Exists(Path.Combine(d, "sub")) && !Directory.Exists(Path.Combine(d, "sub", "sub")));
        w.Close();
    }
}

public class ClipboardTests
{
    static void Slice() => Dispatcher.UIThread.MainLoop(new CancellationTokenSource(20).Token);
    static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Slice(); if (cond()) return true; }
        return cond();
    }

    [AvaloniaFact]
    public void Pastes_files_cut_in_another_file_manager()
    {
        var src = Directory.CreateTempSubdirectory("frost-src-").FullName;
        File.WriteAllText(Path.Combine(src, "from nautilus.txt"), "n");
        var d = Directory.CreateTempSubdirectory("frost-dst-").FullName;
        var w = new MainWindow(d);
        w.Show();
        WaitFor(() => false, 300);
        // Exactly what Nautilus puts on the clipboard for Ctrl+X.
        var data = new DataTransfer();
        var uri = new Uri(Path.Combine(src, "from nautilus.txt")).AbsoluteUri;
        data.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files"), System.Text.Encoding.UTF8.GetBytes("cut\n" + uri)));
        w.Clipboard!.SetDataAsync(data).GetAwaiter().GetResult();
        w.FindControl<ListBox>("List")!.Focus();
        w.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        Assert.True(WaitFor(() => File.Exists(Path.Combine(d, "from nautilus.txt")) && !File.Exists(Path.Combine(src, "from nautilus.txt"))), "cut from another app was not moved here");
        w.Close();
    }

    [AvaloniaFact]
    public void Copy_puts_gnome_and_uri_list_formats_on_the_clipboard()
    {
        var d = Directory.CreateTempSubdirectory("frost-cp-").FullName;
        File.WriteAllText(Path.Combine(d, "a b.txt"), "a");
        var w = new MainWindow(d);
        w.Show();
        WaitFor(() => false, 300);
        var list = w.FindControl<ListBox>("List")!;
        list.SelectedItems!.Add(list.ItemsSource!.Cast<Item>().First());
        list.Focus();
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        WaitFor(() => false, 300);
        using var got = w.Clipboard!.TryGetDataAsync().GetAwaiter().GetResult()!;
        var g = got.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files")).GetAwaiter().GetResult()!;
        Assert.Equal("copy\n" + new Uri(Path.Combine(d, "a b.txt")).AbsoluteUri, System.Text.Encoding.UTF8.GetString(g));
        w.Close();
    }
}

public class DefaultFileManagerMenuTests
{
    static void Slice() => Dispatcher.UIThread.MainLoop(new CancellationTokenSource(20).Token);
    static void Pump(int ms) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) Slice(); }

    static string QueryDefault()
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-mime", new[] { "query", "default", "inode/directory" }) { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return o;
    }

    /// <summary>Opens the "⋯" menu and clicks the item whose text starts with <paramref name="header"/>.</summary>
    static void ClickMoreMenu(MainWindow w, string header)
    {
        w.FindControl<Button>("MoreButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(300);
        var items = MainWindow.LastMenu!.Items.OfType<MenuItem>().ToList();
        string Text(MenuItem m) => (m.Header as Grid)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? m.Header as string ?? "";
        var item = items.FirstOrDefault(m => Text(m).StartsWith(header));
        Assert.True(item != null, $"no menu item \"{header}\"; found: " + string.Join(", ", items.Select(Text)));
        item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindow.LastMenu.Hide();
        Pump(300);
    }

    [AvaloniaFact]
    public void More_menu_makes_Frost_the_default_and_restores_the_previous_one()
    {
        if (!File.Exists("/usr/bin/xdg-mime")) return;
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME")!;
        var apps = Path.Combine(data, "applications");
        Directory.CreateDirectory(apps);
        if (File.Exists(Path.Combine(data, "frost", "previous-default"))) File.Delete(Path.Combine(data, "frost", "previous-default"));
        foreach (var id in new[] { "frost.desktop", "org.example.Files.desktop" })
            File.WriteAllText(Path.Combine(apps, id), "[Desktop Entry]\nType=Application\nName=x\nExec=/usr/bin/true\nMimeType=inode/directory;\n");
        System.Diagnostics.Process.Start("xdg-mime", new[] { "default", "org.example.Files.desktop", "inode/directory" })!.WaitForExit();

        var w = new MainWindow(Directory.CreateTempSubdirectory("frost-m-").FullName);
        w.Show();
        Pump(300);
        ClickMoreMenu(w, "Make Frost the default file manager");
        Assert.Equal("frost.desktop", QueryDefault());
        ClickMoreMenu(w, "Restore previous default file manager");
        Assert.Equal("org.example.Files.desktop", QueryDefault());
        w.Close();
    }
}
