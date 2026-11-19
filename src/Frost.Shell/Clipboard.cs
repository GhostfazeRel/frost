using System.Collections.Specialized;
using System.IO;
using System.Windows;

namespace Frost.Shell;

public static class FileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";

    private static T? Retry<T>(Func<T> f)
    {
        for (int i = 0; i < 8; i++)
        {
            try { return f(); }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(40); } // CLIPBRD_E_CANT_OPEN
            catch { return default; }
        }
        return default;
    }

    public static void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        Retry<object?>(() =>
        {
            var sc = new StringCollection();
            sc.AddRange(paths.ToArray());
            var data = new DataObject();
            data.SetFileDropList(sc);
            data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            Clipboard.SetDataObject(data, true);
            return null;
        });
    }

    public static IReadOnlyList<string> GetFiles(out bool cut)
    {
        bool c = false;
        var files = Retry(() =>
        {
            var d = Clipboard.GetDataObject();
            if (d == null || !d.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
            if (d.GetData(DropEffectFormat) is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.Position = 0;
                ms.ReadExactly(b);
                c = (BitConverter.ToInt32(b) & 2) != 0;
            }
            return (d.GetData(DataFormats.FileDrop) as string[])?.ToList() ?? new List<string>();
        }) ?? new List<string>();
        cut = c && files.Count > 0;
        return files;
    }

    public static bool HasFiles() => Retry(() => Clipboard.ContainsFileDropList());
}
