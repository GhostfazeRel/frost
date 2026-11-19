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
    /// <summary>Set from <c>--wait-pid N</c>: the old Frost that started this update; Install waits for it to exit.</summary>
    public static int? WaitPid { get; set; }
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Frost.lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Frost.lnk");

    public static bool IsSetupMode(string[] args) =>
        args.Contains("--install", StringComparer.OrdinalIgnoreCase) ||
        Path.GetFileName(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetDirectoryName(p), Dir, StringComparison.OrdinalIgnoreCase);

    public const string CloseFrostMessage = "Frost is running. Close every Frost window, then try again.";

    /// <summary>True if a Frost started from the install folder is running (other than this process).</summary>
    public static bool InstalledCopyRunning()
    {
        foreach (var p in Process.GetProcessesByName("Frost"))
            using (p)
                try
                {
                    if (p.Id != Environment.ProcessId && string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), Dir, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { } // no access to that process: not ours to worry about
        return false;
    }

    /// <summary>Waits for --wait-pid, then copies this exe into the install dir. Slow: run off the UI thread.
    /// Returns true if Frost was already installed. Throws with a user-facing message on failure.</summary>
    public static bool CopySelf()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Frost's own file.");
        if (WaitPid is { } pid)
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(15000); } catch { } // already gone = fine
        var upgrading = File.Exists(Exe);
        Directory.CreateDirectory(Dir);
        if (!string.Equals(Path.GetFullPath(self), Exe, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(self, Exe, true); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // sharing / lock violation
            {
                throw new InvalidOperationException(CloseFrostMessage);
            }
        }
        return upgrading;
    }

    /// <summary>Shortcuts, Apps entry, default hook. Throws with a user-facing message on failure.</summary>
    public static void Register(bool upgrading, bool startMenu, bool desktop, bool makeDefault)
    {
        // The boxes reflect the existing install, so unticked means "remove it".
        SetShortcut(StartMenuLink, startMenu, upgrading);
        SetShortcut(DesktopLink, desktop, upgrading);

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
            catch { DefaultFileManager.Disable(Exe); throw; } // never leave a half-written hook behind
        }
        else DefaultFileManager.Disable(Exe); // no-op unless our hook is there

        var s = SettingsStore.Load(); // the setup checkbox was the user's answer: no first-run prompt
        s.AskedDefault = true;
        SettingsStore.Save(s);
    }

    private static void SetShortcut(string lnk, bool wanted, bool upgrading)
    {
        if (wanted) ShellActions.CreateShortcut(lnk, Exe, "Frost file manager");
        else if (upgrading && File.Exists(lnk)) File.Delete(lnk); // only ours to remove when the box showed it existing
    }

    /// <summary>Removes everything Install created except %AppData%\Frost. Never throws. Callers make sure no other installed Frost runs.</summary>
    public static void Uninstall()
    {
        DefaultFileManager.Disable(Exe);
        foreach (var lnk in new[] { StartMenuLink, DesktopLink }) try { File.Delete(lnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (!IsInstalled || !Path.IsPathFullyQualified(Dir)) return; // only the installed copy removes its own folder
        try
        {
            // Our exe is still running: let it exit first, then remove the folder.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Dir}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        catch { }
    }
}
