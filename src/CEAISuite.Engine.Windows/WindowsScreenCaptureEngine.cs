using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Captures screenshots of process windows using Win32 GDI (PrintWindow).
/// Falls back to BitBlt screen capture if PrintWindow fails.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCaptureEngine : IScreenCaptureEngine
{
    public Task<ScreenCaptureResult?> CaptureWindowAsync(int processId, CancellationToken ct = default)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return Task.FromResult<ScreenCaptureResult?>(null);

            if (!GetWindowRect(hwnd, out var rect))
                return Task.FromResult<ScreenCaptureResult?>(null);

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return Task.FromResult<ScreenCaptureResult?>(null);

            // Cap dimensions to avoid huge allocations
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

            // Convert to PNG bytes
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            var pngBytes = ms.ToArray();

            string title = process.MainWindowTitle ?? process.ProcessName;
            return Task.FromResult<ScreenCaptureResult?>(
                new ScreenCaptureResult(pngBytes, width, height, title));
        }
        catch
        {
            return Task.FromResult<ScreenCaptureResult?>(null);
        }
    }

    private const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
