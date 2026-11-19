using System.IO;
using System.Runtime.InteropServices;

namespace Frost.Shell;

public static class Places
{
    private static readonly (string Name, string Id, string Glyph)[] Known =
    {
        ("Home", "5E6C858F-0E22-4760-9AFE-EA3317B67173", ""),
        ("Desktop", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641", ""),
        ("Downloads", "374DE290-123F-4565-9164-39C4925E467B", ""),
        ("Documents", "FDD39AD0-238F-46AF-ADB4-6C85480369C7", ""),
        ("Pictures", "33E28130-4E1E-4676-835A-98395C3BC3BB", ""),
        ("Music", "4BD8D571-6D19-48D3-BE97-422220080E43", ""),
        ("Videos", "18989B1D-99B5-455B-841C-AB7C74E4DDFC", ""),
    };

    /// <summary>Home, Desktop, Downloads, Documents, Pictures, Music, Videos (only those that exist).</summary>
    public static IReadOnlyList<QuickFolder> QuickAccess()
    {
        var list = new List<QuickFolder>();
        foreach (var (name, id, glyph) in Known)
        {
            try
            {
                var g = new Guid(id);
                IntPtr p = IntPtr.Zero;
                try
                {
                    if (Native.SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) < 0) continue;
                    var path = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new QuickFolder(name, path, glyph));
                }
                finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
            }
            catch { }
        }
        return list;
    }

    /// <summary>Ready drives only; each drive is probed on the thread pool with a timeout (1s network, 5s other).</summary>
    public static IReadOnlyList<DriveItem> Drives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { return Array.Empty<DriveItem>(); }

        var tasks = drives.Select(d => (d, Task.Run(() => Probe(d)))).ToList();
        var result = new List<DriveItem>();
        foreach (var (d, t) in tasks)
        {
            try
            {
                if (t.Wait(d.DriveType == DriveType.Network ? 1000 : 5000) && t.Result is { } item) result.Add(item);
            }
            catch { }
        }
        return result;
    }

    private static DriveItem? Probe(DriveInfo d)
    {
        try
        {
            if (!d.IsReady) return null;
            var kind = d.DriveType switch
            {
                DriveType.Fixed => DriveKind.Fixed, DriveType.Removable => DriveKind.Removable,
                DriveType.Network => DriveKind.Network, DriveType.CDRom => DriveKind.CdRom, _ => DriveKind.Other,
            };
            string label = "";
            try { label = d.VolumeLabel; } catch { }
            if (string.IsNullOrWhiteSpace(label))
                label = kind switch
                {
                    DriveKind.Fixed => "Local Disk", DriveKind.Removable => "Removable Disk",
                    DriveKind.Network => "Network Drive", DriveKind.CdRom => "CD Drive", _ => "Drive",
                };
            string root = d.RootDirectory.FullName;
            return new DriveItem($"{label} ({root.TrimEnd('\\', '/')})", label, root, d.TotalSize, d.AvailableFreeSpace, kind);
        }
        catch { return null; }
    }
}
