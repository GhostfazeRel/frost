using System.Runtime.InteropServices;

namespace Frost.Shell;

// x64 layouts (default packing). Vtable order of every [ComImport] interface follows the Windows SDK headers.
internal static class Native
{
    public const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 1, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_SYSICONINDEX = 0x4000;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    public static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        public string? lpVerb; public string? lpFile; public string? lpParameters; public string? lpDirectory;
        public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; public string? lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public IntPtr pFrom; public IntPtr pTo; public ushort fFlags;
        public int fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OPENASINFO
    {
        public string pcszFile; public string? pcszClass; public uint oaifInFlags;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int l, r, t, b; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public IntPtr lpVerb; public IntPtr lpParameters;
        public IntPtr lpDirectory; public int nShow; public uint dwHotKey; public IntPtr hIcon; public IntPtr lpTitle;
        public IntPtr lpVerbW; public IntPtr lpParametersW; public IntPtr lpDirectoryW; public IntPtr lpTitleW; public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("shell32.dll")] public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHOpenWithDialog(IntPtr hwnd, ref OPENASINFO info);

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int cb, out BITMAP bmp);
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
}

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
    [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint attrs);
    [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr strret);
    [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
    [PreserveSig] int InvokeCommand(ref Native.CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
}

[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
    [PreserveSig] int InvokeCommand(ref Native.CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
    [PreserveSig] int InvokeCommand(ref Native.CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(Native.SIZE size, uint flags, out IntPtr phbm);
}

[ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
    [PreserveSig] int Draw(IntPtr pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, uint flags, out IntPtr picon);
}
