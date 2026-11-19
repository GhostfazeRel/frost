using Microsoft.Win32;

namespace Frost.Shell;

/// <summary>Per-user (HKCU only) "open folders, drives and Win+E in Frost" hook, the same approach the Files app uses.
/// Whatever another app had there is saved on Enable and put back on Disable; entries that don't point at our exe are never touched.</summary>
public static class DefaultFileManager
{
    private const string Verb = "openinfrost";
    private const string PrevDefault = "FrostPrevDefault", PrevDelegate = "FrostPrevDelegate";
    private const string ClsidKey = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";
    private const string WinECommand = ClsidKey + @"\shell\opennewwindow\command";
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

    private static bool Ours(RegistryKey? command, string exePath) =>
        command?.GetValue("") is string s && s.Contains(exePath, StringComparison.OrdinalIgnoreCase);

    public static void Enable(string exePath)
    {
        foreach (var path in ShellKeys)
        {
            using var shell = Registry.CurrentUser.CreateSubKey(path);
            using var verb = shell.CreateSubKey(Verb);
            if (shell.GetValue("") is string prev && prev.Length > 0 && prev != Verb) verb.SetValue(PrevDefault, prev); // e.g. "openinfiles"
            verb.SetValue("", "Open in Frost");
            using (var cmd = verb.CreateSubKey("command")) cmd.SetValue("", $"\"{exePath}\" \"%1\"");
            shell.SetValue("", Verb);
        }
        using (var cmd = Registry.CurrentUser.CreateSubKey(WinECommand))
        {
            if (cmd.GetValue("") is string prev && !Ours(cmd, exePath)) // another app owns Win+E: remember it
            {
                cmd.SetValue(PrevDefault, prev);
                if (cmd.GetValue("DelegateExecute") is string d) cmd.SetValue(PrevDelegate, d);
            }
            cmd.SetValue("", $"\"{exePath}\"");
            cmd.SetValue("DelegateExecute", "");
        }
        Notify();
    }

    /// <summary>Undoes <see cref="Enable"/> for this exe and restores what was there before. Never throws.</summary>
    public static void Disable(string exePath)
    {
        foreach (var path in ShellKeys)
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey(path, true);
                using var verb = shell?.OpenSubKey(Verb);
                if (shell == null || verb == null) continue;
                using (var cmd = verb.OpenSubKey("command"))
                    if (!Ours(cmd, exePath)) continue;
                if (shell.GetValue("") as string == Verb)
                {
                    if (verb.GetValue(PrevDefault) is string prev) shell.SetValue("", prev);
                    else shell.DeleteValue("", false);
                }
                verb.Dispose();
                shell.DeleteSubKeyTree(Verb, false);
            }
            catch { }
        }
        try
        {
            bool restored = false;
            using (var cmd = Registry.CurrentUser.OpenSubKey(WinECommand, true))
            {
                if (!Ours(cmd, exePath)) { Notify(); return; }
                if (cmd!.GetValue(PrevDefault) is string prev)
                {
                    restored = true;
                    cmd.SetValue("", prev);
                    if (cmd.GetValue(PrevDelegate) is string d) cmd.SetValue("DelegateExecute", d);
                    else cmd.DeleteValue("DelegateExecute", false);
                    cmd.DeleteValue(PrevDefault, false);
                    cmd.DeleteValue(PrevDelegate, false);
                }
            }
            if (!restored)
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClsidKey + @"\shell\opennewwindow", false);
                DeleteIfEmpty(ClsidKey + @"\shell"); // the parents are ours too unless something else lives in them
                DeleteIfEmpty(ClsidKey);
            }
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
