using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;

namespace Frost.Shell;

public static class ShellActions
{
    private static IntPtr Hwnd(Window? w)
    {
        try { return w == null ? IntPtr.Zero : new WindowInteropHelper(w).EnsureHandle(); } catch { return IntPtr.Zero; }
    }

    private static void Exec(string path, string? verb, uint mask)
    {
        try
        {
            var sei = new Native.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(), fMask = mask, lpVerb = verb, lpFile = path,
                lpDirectory = Path.GetDirectoryName(path), nShow = 1,
            };
            Native.ShellExecuteEx(ref sei);
        }
        catch { }
    }

    // Null verb = the file's default verb (more robust than a literal "open" for e.g. media-only types).
    public static void Open(string path) => Exec(path, null, 0x100 /*SEE_MASK_NOASYNC*/);

    public static void ShowProperties(string path) => Exec(path, "properties", 0xC /*SEE_MASK_INVOKEIDLIST*/);

    public static void OpenWith(string path)
    {
        try
        {
            var info = new Native.OPENASINFO { pcszFile = path, pcszClass = null, oaifInFlags = 1 | 2 | 4 };
            if (Native.SHOpenWithDialog(IntPtr.Zero, ref info) >= 0) return;
        }
        catch { }
        try { Process.Start(new ProcessStartInfo("rundll32.exe") { ArgumentList = { "shell32.dll,OpenAs_RunDLL", path } }); } catch { }
    }

    public static void OpenTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = false });
            return;
        }
        catch { }
        try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = false }); } catch { }
    }

    public static bool Recycle(IReadOnlyList<string> paths, Window owner)
    {
        if (paths.Count == 0) return true;
        IntPtr from = IntPtr.Zero;
        try
        {
            from = Marshal.StringToHGlobalUni(string.Join("\0", paths) + "\0\0");
            var op = new Native.SHFILEOPSTRUCT
            {
                hwnd = Hwnd(owner), wFunc = 3 /*FO_DELETE*/, pFrom = from,
                fFlags = 0x40 /*FOF_ALLOWUNDO*/ | 0x4000 /*FOF_WANTNUKEWARNING*/,
            };
            int r = Native.SHFileOperation(ref op);
            return r == 0 && op.fAnyOperationsAborted == 0;
        }
        catch { return false; }
        finally { if (from != IntPtr.Zero) Marshal.FreeHGlobal(from); }
    }

    public static void ShowNativeContextMenu(Window owner, string folder, IReadOnlyList<string> paths, Point screenPoint)
    {
        try { ContextMenuHost.Show(Hwnd(owner), folder, paths, (int)screenPoint.X, (int)screenPoint.Y); }
        catch { }
    }

    /// <summary>Writes a .lnk at <paramref name="lnkPath"/> pointing to <paramref name="target"/>.</summary>
    public static void CreateShortcut(string lnkPath, string target, string description)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            Marshal.ThrowExceptionForHR(link.SetPath(target));
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target, 0);
            link.SetDescription(description);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }
}

internal static class ContextMenuHost
{
    private const uint CMF_NORMAL = 0, CMF_EXPLORE = 4, CMF_EXTENDEDVERBS = 0x100;
    private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    private const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
    private const uint FirstId = 1, LastId = 0x7FFF;

    public static void Show(IntPtr hwnd, string folder, IReadOnlyList<string> paths, int x, int y)
    {
        if (hwnd == IntPtr.Zero) return;
        var pidls = new List<IntPtr>();
        IntPtr childArr = IntPtr.Zero, menu = IntPtr.Zero, cmPtr = IntPtr.Zero, parentPtr = IntPtr.Zero;
        IShellFolder? parent = null, bgFolder = null;
        object? cm = null;
        HwndSource? src = null;
        HwndSourceHook? hook = null;
        try
        {
            bool background = paths.Count == 0;
            var targets = background ? new[] { folder } : paths.ToArray();
            var children = new List<IntPtr>();
            var iidFolder = Native.IID_IShellFolder;
            foreach (var p in targets)
            {
                if (Native.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) < 0 || pidl == IntPtr.Zero) continue;
                pidls.Add(pidl);
                if (parent == null)
                {
                    if (Native.SHBindToParent(pidl, ref iidFolder, out parentPtr, out var last) < 0) return;
                    parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                    children.Add(last);
                }
                else
                {
                    if (Native.SHBindToParent(pidl, ref iidFolder, out var pp, out var last) >= 0) { children.Add(last); Marshal.Release(pp); }
                }
            }
            if (parent == null || children.Count == 0) return;

            var iidCm = Native.IID_IContextMenu;
            if (background)
            {
                if (parent.BindToObject(children[0], IntPtr.Zero, ref iidFolder, out var fp) < 0) return;
                bgFolder = (IShellFolder)Marshal.GetObjectForIUnknown(fp);
                Marshal.Release(fp);
                if (bgFolder.CreateViewObject(hwnd, ref iidCm, out cmPtr) < 0) return;
            }
            else
            {
                childArr = Marshal.AllocCoTaskMem(IntPtr.Size * children.Count);
                for (int i = 0; i < children.Count; i++) Marshal.WriteIntPtr(childArr, i * IntPtr.Size, children[i]);
                if (parent.GetUIObjectOf(hwnd, (uint)children.Count, childArr, ref iidCm, IntPtr.Zero, out cmPtr) < 0) return;
            }
            if (cmPtr == IntPtr.Zero) return;
            cm = Marshal.GetObjectForIUnknown(cmPtr);
            var cm1 = (IContextMenu)cm;
            var cm2 = cm as IContextMenu2;
            var cm3 = cm as IContextMenu3;

            menu = Native.CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | (Native.GetKeyState(0x10) < 0 ? CMF_EXTENDEDVERBS : 0);
            if (cm1.QueryContextMenu(menu, 0, FirstId, LastId, flags) < 0) return;

            src = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    try
                    {
                        if (cm3 != null) { cm3.HandleMenuMsg2((uint)msg, w, l, out var res); handled = true; return res; }
                        if (cm2 != null && msg != WM_MENUCHAR) { cm2.HandleMenuMsg((uint)msg, w, l); handled = true; return IntPtr.Zero; }
                    }
                    catch { }
                }
                return IntPtr.Zero;
            };
            src?.AddHook(hook);

            Native.SetForegroundWindow(hwnd);
            int cmd = Native.TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
            Native.PostMessage(hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            if (cmd <= 0) return;

            var verb = (IntPtr)(cmd - (int)FirstId);
            var ici = new Native.CMINVOKECOMMANDINFOEX
            {
                fMask = 0x4000 /*CMIC_MASK_UNICODE*/ | 0x20000000 /*CMIC_MASK_PTINVOKE*/, hwnd = hwnd,
                lpVerb = verb, lpVerbW = verb, nShow = 1, ptInvoke = new Native.POINT { x = x, y = y },
            };
            ici.cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>();
            cm1.InvokeCommand(ref ici);
        }
        finally
        {
            if (hook != null) src?.RemoveHook(hook);
            if (menu != IntPtr.Zero) Native.DestroyMenu(menu);
            if (childArr != IntPtr.Zero) Marshal.FreeCoTaskMem(childArr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (bgFolder != null) Marshal.ReleaseComObject(bgFolder);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
        }
    }
}
