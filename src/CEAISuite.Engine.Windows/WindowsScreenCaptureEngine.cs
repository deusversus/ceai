using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Captures screenshots of process windows using Win32 GDI.
/// Enumerates all top-level windows belonging to the process to find the game window,
/// since Process.MainWindowHandle is unreliable for many games.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCaptureEngine : IScreenCaptureEngine
{
    public Task<ScreenCaptureResult?> CaptureWindowAsync(int processId, CancellationToken ct = default)
    {
        try
        {
            var hwnd = FindBestWindow(processId);
            if (hwnd == IntPtr.Zero)
                return Task.FromResult<ScreenCaptureResult?>(null);

            if (!GetWindowRect(hwnd, out var rect))
                return Task.FromResult<ScreenCaptureResult?>(null);

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return Task.FromResult<ScreenCaptureResult?>(null);

            const int maxDim = 3840;
            if (width > maxDim) width = maxDim;
            if (height > maxDim) height = maxDim;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            // Try PrintWindow first (works with DWM-composed windows)
            bool captured = false;
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    captured = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            // Fallback: BitBlt from screen coordinates
            if (!captured)
            {
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            var pngBytes = ms.ToArray();

            // Get window title
            var titleBuf = new char[256];
            int titleLen = GetWindowText(hwnd, titleBuf, titleBuf.Length);
            string title = titleLen > 0 ? new string(titleBuf, 0, titleLen) : $"PID {processId}";

            return Task.FromResult<ScreenCaptureResult?>(
                new ScreenCaptureResult(pngBytes, width, height, title));
        }
        catch
        {
            return Task.FromResult<ScreenCaptureResult?>(null);
        }
    }

    /// <summary>
    /// Find the best (largest visible) window belonging to a process.
    /// Process.MainWindowHandle is unreliable for games — this enumerates
    /// all top-level windows and picks the largest visible, non-minimized one.
    /// </summary>
    private static IntPtr FindBestWindow(int processId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != (uint)processId) return true; // continue

            // Skip invisible and minimized windows
            if (!IsWindowVisible(hwnd)) return true;
            if (IsIconic(hwnd)) return true;

            // Skip tool windows (tooltips, floating palettes)
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            if (GetWindowRect(hwnd, out var rect))
            {
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }
            }

            return true; // continue enumeration
        }, IntPtr.Zero);

        // Fallback to .NET MainWindowHandle if enumeration found nothing
        if (best == IntPtr.Zero)
        {
            try
            {
                best = Process.GetProcessById(processId).MainWindowHandle;
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[WindowsScreenCaptureEngine] Failed to get MainWindowHandle (process may have exited): {ex.Message}"); }
        }

        return best;
    }

    private const uint PW_RENDERFULLCONTENT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint nFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, char[] lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
