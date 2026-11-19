using System.Diagnostics;
using Frost.Shell;
using Microsoft.Win32;

namespace Frost.App;

/// <summary>Per-user install into %LocalAppData%\Programs\Frost (HKCU only, no admin).</summary>
public static class Installer
{
    public static readonly string Version = typeof(Installer).Assembly.GetName().Version!.ToString(3);
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Frost");
    public static readonly string Exe = Path.Combine(Dir, "Frost.exe");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Frost";
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Frost.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Frost.lnk");

    public static bool IsSetupMode(string[] args) =>
        args.Contains("--install", StringComparer.OrdinalIgnoreCase) ||
        Path.GetFileName(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetDirectoryName(p), Dir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws with a user-facing message on failure.</summary>
    public static void Install(bool startMenu, bool desktop, bool makeDefault)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Frost's own file.");
        Directory.CreateDirectory(Dir);
        if (!string.Equals(Path.GetFullPath(self), Exe, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(self, Exe, true); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing / lock violation
            {
                throw new InvalidOperationException("Frost is running. Close every Frost window, then click Install again.");
            }
        }
        if (startMenu) ShellActions.CreateShortcut(StartMenuLink, Exe, "Frost file manager");
        if (desktop) ShellActions.CreateShortcut(DesktopLink, Exe, "Frost file manager");

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Frost");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "GhostfazeRel");
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
        }

        if (makeDefault)
        {
            try { DefaultFileManager.Enable(Exe); }
            catch { DefaultFileManager.Disable(); throw; } // never leave a half-written hook behind
        }

        var s = SettingsStore.Load(); // the setup checkbox was the user's answer: no first-run prompt
        s.AskedDefault = true;
        SettingsStore.Save(s);
    }

    /// <summary>Removes everything Install created except %AppData%\Frost. Never throws.</summary>
    public static void Uninstall()
    {
        DefaultFileManager.Disable();
        foreach (var lnk in new[] { StartMenuLink, DesktopLink }) try { File.Delete(lnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (!Path.IsPathFullyQualified(Dir)) return; // never rmdir a relative path
        try
        {
            // Our exe is still running: let it exit first, then remove the folder.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Dir}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        catch { }
    }
}
