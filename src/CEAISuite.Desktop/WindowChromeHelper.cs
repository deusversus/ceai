using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace CEAISuite.Desktop;

/// <summary>
/// Shared helpers for custom title bar caption buttons and Windows 11 snap layouts.
/// </summary>
public static class WindowChromeHelper
{
    public static void Minimize(Window w) => w.WindowState = WindowState.Minimized;

    public static void MaximizeRestore(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    public static void Close(Window w) => w.Close();

    /// <summary>
    /// Installs a WndProc hook that returns HTMAXBUTTON when the mouse is over <paramref name="maximizeButton"/>,
    /// enabling the Windows 11 snap layout flyout on hover.
    /// Also compensates for the maximize border offset.
    /// </summary>
    public static void EnableSnapLayout(Window window, Button maximizeButton, FrameworkElement rootElement)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            // WM_NCHITTEST
            if (msg == 0x0084 && maximizeButton != null)
            {
                var screenPoint = PointFromLParam(lParam);
                try
                {
                    var btnPoint = maximizeButton.PointFromScreen(screenPoint);
                    if (btnPoint.X >= 0 && btnPoint.X <= maximizeButton.ActualWidth &&
                        btnPoint.Y >= 0 && btnPoint.Y <= maximizeButton.ActualHeight)
                    {
                        handled = true;
                        return new IntPtr(9); // HTMAXBUTTON
                    }
                }
                catch
                {
                    // Button not yet in visual tree
                }
            }

            // WM_GETMINMAXINFO — prevent maximized window from covering taskbar
            if (msg == 0x0024)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = false; // let default processing continue
            }

            return IntPtr.Zero;
        });

        // Compensate for the invisible resize border when maximized
        window.StateChanged += (_, _) =>
        {
            if (rootElement != null)
            {
                rootElement.Margin = window.WindowState == WindowState.Maximized
                    ? new Thickness(7) : new Thickness(0);
            }
        };
    }

    private static Point PointFromLParam(IntPtr lParam)
    {
        int xy = lParam.ToInt32();
        int x = (short)(xy & 0xFFFF);
        int y = (short)((xy >> 16) & 0xFFFF);
        return new Point(x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    // ─── DWM Rounded Corners (Windows 11) ──────────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Asks Windows 11 to apply native rounded corners to the window.
    /// No-op on Windows 10 or earlier (the API call simply fails silently).
    /// Call from OnSourceInitialized or Loaded after the HWND exists.
    /// </summary>
    public static void EnableRoundedCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Pre-Win11 or API unavailable — just stay square
        }
    }

    // ─── Monitor helpers ─────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            var rcWork = mi.rcWork;
            var rcMonitor = mi.rcMonitor;
            mmi.ptMaxPosition.x = Math.Abs(rcWork.left - rcMonitor.left);
            mmi.ptMaxPosition.y = Math.Abs(rcWork.top - rcMonitor.top);
            mmi.ptMaxSize.x = Math.Abs(rcWork.right - rcWork.left);
            mmi.ptMaxSize.y = Math.Abs(rcWork.bottom - rcWork.top);
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }
}
