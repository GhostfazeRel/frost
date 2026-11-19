using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Frost.Shell;

public static class Backdrop
{
    public static bool TryApply(Window window, bool dark)
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22621) return false;
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return false;

            int v = dark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); // dark mode: cosmetic, ignore failure
            v = 3; // DWMSBT_TRANSIENTWINDOW (acrylic)
            if (Native.DwmSetWindowAttribute(hwnd, 38, ref v, sizeof(int)) < 0) return false;
            v = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33, ref v, sizeof(int));

            var m = new Native.MARGINS { l = -1, r = -1, t = -1, b = -1 };
            if (Native.DwmExtendFrameIntoClientArea(hwnd, ref m) < 0) return false;
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch { return false; }
    }
}
