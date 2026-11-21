using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Frost.Linux;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Frost.Linux.Tests.TestApp))]
// Several tests share the sandboxed mimeapps.list, so run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Frost.Linux.Tests;

public static class TestApp
{
    // Keep the user's real settings and trash out of the tests. Not /tmp: gio can't trash on tmpfs.
    [ModuleInitializer]
    public static void Isolate()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "frost-tests");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TMPDIR", root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateDirectory(Path.Combine(root, "config")).FullName);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(root, "cache")); // thumbnails and undo backups
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class PlatformTests
{
    static string Sandbox() { var d = Directory.CreateTempSubdirectory("frost-t-").FullName; File.WriteAllText(Path.Combine(d, "a.txt"), "a"); return d; }

    [Fact]
    public void StartPath_accepts_folders_files_and_uris()
    {
        var d = Sandbox();
        Assert.Equal(d, Platform.StartPath(d));
        Assert.Equal(d, Platform.StartPath(Path.Combine(d, "a.txt")));
        Assert.Equal(d, Platform.StartPath(new Uri(d).AbsoluteUri));
        Assert.Null(Platform.StartPath(Path.Combine(d, "missing")));
    }

    [Fact]
    public void Trash_moves_file_away_without_deleting_others()
    {
        if (!File.Exists("/usr/bin/gio")) return; // gio is what Frost trashes with
        var d = Sandbox();
        File.WriteAllText(Path.Combine(d, "keep.txt"), "k");
        Assert.True(Platform.Trash(new[] { Path.Combine(d, "a.txt") }));
        Assert.False(File.Exists(Path.Combine(d, "a.txt")));
        Assert.True(File.Exists(Path.Combine(d, "keep.txt")));
    }

    [Fact]
    public void Default_file_manager_round_trip_restores_the_previous_one()
    {
        if (!File.Exists("/usr/bin/xdg-mime")) return;
        // XDG_CONFIG_HOME/XDG_DATA_HOME point at the test sandbox, so the real mimeapps.list is untouched.
        // xdg-mime only reports handlers whose .desktop file exists.
        var apps = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME")!, "applications");
        Directory.CreateDirectory(apps);
        foreach (var id in new[] { "frost.desktop", "org.example.Files.desktop" })
            File.WriteAllText(Path.Combine(apps, id), "[Desktop Entry]\nType=Application\nName=x\nExec=/usr/bin/true\nMimeType=inode/directory;\n");
        Process.Start("xdg-mime", new[] { "default", "org.example.Files.desktop", "inode/directory" })!.WaitForExit();
        Assert.False(Platform.IsDefaultFileManager());
        Assert.Null(Platform.MakeDefaultFileManager());
        Assert.True(Platform.IsDefaultFileManager());
        Assert.Null(Platform.MakeDefaultFileManager()); // again: must not forget the real previous one
        Assert.Null(Platform.RestoreDefaultFileManager());
        Assert.False(Platform.IsDefaultFileManager());
        var p = Process.Start(new ProcessStartInfo("xdg-mime", new[] { "query", "default", "inode/directory" }) { RedirectStandardOutput = true })!;
        Assert.Equal("org.example.Files.desktop", p.StandardOutput.ReadToEnd().Trim());
    }

    [Fact]
    public void Places_include_home_and_root()
    {
        Assert.Equal(Platform.Home, Platform.QuickAccess()[0].Path);
        Assert.Equal("/", Platform.Drives()[0].Path);
    }
}

public class WindowTests
{
    // Run the real dispatcher loop (not just RunJobs) so DispatcherTimers fire too.
    static void Slice() => Dispatcher.UIThread.MainLoop(new CancellationTokenSource(20).Token);

    static void Pump(int ms = 400)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) Slice();
    }

    static (MainWindow w, string dir) Open()
    {
        var d = Directory.CreateTempSubdirectory("frost-w-").FullName;
        File.WriteAllText(Path.Combine(d, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(d, "sub"));
        var w = new MainWindow(d);
        w.Show();
        Pump();
        return (w, d);
    }

    static string[] Listed(MainWindow w) =>
        w.FindControl<ListBox>("List")!.ItemsSource!.Cast<Item>().Select(i => i.Name).OrderBy(n => n).ToArray();

    [AvaloniaFact]
    public void Opens_the_folder_from_the_command_line()
    {
        var (w, _) = Open();
        Assert.Equal(new[] { "a.txt", "sub" }, Listed(w));
        w.Close();
    }

    [AvaloniaFact]
    public void CtrlShiftN_creates_a_folder_and_CtrlZ_undoes_it()
    {
        var (w, d) = Open();
        w.KeyPress(Key.N, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.N, "N");
        Pump();
        var created = Directory.GetDirectories(d).Select(Path.GetFileName).Except(new[] { "sub" }).ToArray();
        Assert.Single(created);
        // Close the rename prompt that follows, then undo.
        foreach (var win in w.OwnedWindows.ToList()) win.Close();
        Pump();
        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Pump();
        Assert.Equal(new[] { "sub" }, Directory.GetDirectories(d).Select(Path.GetFileName).ToArray());
        w.Close();
    }

    [AvaloniaFact]
    public void Copy_and_paste_duplicates_selected_file()
    {
        var (w, d) = Open();
        var list = w.FindControl<ListBox>("List")!;
        list.SelectedItem = list.ItemsSource!.Cast<Item>().First(i => i.Name == "a.txt");
        list.Focus();
        w.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        w.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        Pump(800);
        Assert.Equal(2, Directory.GetFiles(d).Length);
        w.Close();
    }
}
