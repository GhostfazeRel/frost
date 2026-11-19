using Microsoft.Win32;

namespace Frost.Shell;

/// <summary>Per-user (HKCU only) "open folders, drives and Win+E in Frost" hook, the same approach the Files app uses.</summary>
public static class DefaultFileManager
{
    private const string Verb = "openinfrost";
    private const string ClsidKey = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
    private static readonly string[] ShellKeys = { @"Software\Classes\Directory\shell", @"Software\Classes\Drive\shell" };

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(ShellKeys[0]);
            return k?.GetValue("") as string == Verb;
        }
        catch { return false; }
    }

    public static void Enable(string exePath)
    {
        foreach (var path in ShellKeys)
        {
            using var shell = Registry.CurrentUser.CreateSubKey(path);
            shell.SetValue("", Verb);
            using var verb = shell.CreateSubKey(Verb);
            verb.SetValue("", "Open in Frost");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        using (var cmd = Registry.CurrentUser.CreateSubKey(ClsidKey + @"\shell\opennewwindow\command"))
        {
            cmd.SetValue("", $"\"{exePath}\"");
            cmd.SetValue("DelegateExecute", "");
        }
        Notify();
    }

    /// <summary>Removes only what <see cref="Enable"/> created. Never throws.</summary>
    public static void Disable()
    {
        foreach (var path in ShellKeys)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey(path, true);
                if (shell == null) continue;
                if (shell.GetValue("") as string == Verb) shell.DeleteValue("", false);
                shell.DeleteSubKeyTree(Verb, false);
            }
            catch { }
        }
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClsidKey + @"\shell\opennewwindow", false);
            // The parents are ours too unless something else lives in them.
            DeleteIfEmpty(ClsidKey + @"\shell");
            DeleteIfEmpty(ClsidKey);
        }
        catch { }
        Notify();
    }

    private static void DeleteIfEmpty(string path)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(path))
            if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(path, false);
    }

    private static void Notify()
    {
        try { Native.SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0 /*SHCNF_IDLIST*/, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}
