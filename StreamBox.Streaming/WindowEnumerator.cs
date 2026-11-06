using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamBox.Streaming;

public sealed record WindowInfo(nint Handle, string Title, string ProcessName, string ClassName, int Width, int Height, bool IsForeground);

public sealed record WindowThumbnail(byte[] Bgra, int Width, int Height);

/// <summary>Lista janelas capturáveis e gera miniaturas estáticas para a interface.</summary>
public static class WindowEnumerator
{
    private const int DwmwaCloaked = 14;
    private const uint PwRenderFullContent = 0x00000002;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private static readonly string[] IgnoredClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ApplicationManager_DesktopShellWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
    ];

    public static IReadOnlyList<WindowInfo> List(int maxHeight = 1080)
    {
        var self = Process.GetCurrentProcess();
        var foreground = GetForegroundWindow();
        var results = new List<WindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (hwnd == IntPtr.Zero) return true;
                if (!IsWindowVisible(hwnd)) return true;
                if (IsIconic(hwnd)) return true;

                var length = GetWindowTextLength(hwnd);
                if (length == 0) return true;

                var title = new StringBuilder(length + 1);
                if (GetWindowText(hwnd, title, title.Capacity) == 0) return true;
                var cleanTitle = title.ToString().Trim();
                if (cleanTitle.Length == 0) return true;

                if ((GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;

                var className = GetClassNameOf(hwnd);
                if (IgnoredClasses.Contains(className, StringComparer.Ordinal)) return true;

                if (IsCloaked(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == self.Id) return true;

                string processName;
                try { processName = Process.GetProcessById((int)pid).ProcessName; }
                catch { return true; }

                if (!TryGetWindowSize(hwnd, out var width, out var height)) return true;
                if (width < 160 || height < 120) return true;

                results.Add(new WindowInfo(hwnd, cleanTitle, processName, className, width, height, hwnd == foreground));
            }
            catch
            {
                // Janelas que desaparecem durante a enumeração são simplesmente ignoradas.
            }

            return true;
        }, IntPtr.Zero);

        return results
            .OrderByDescending(w => w.IsForeground)
            .ThenBy(w => w.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool TryGetWindowSize(nint hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (GetWindowRect(hwnd, out var rect))
        {
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width > 0 && height > 0;
        }

        return false;
    }

    /// <summary>Miniatura estática da janela, capturada por PrintWindow (sem custo contínuo).</summary>
    public static WindowThumbnail? CaptureThumbnail(nint hwnd, int targetWidth = 360)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            Process process;
            try { process = Process.GetProcessById((int)pid); }
            catch { return null; }

            if (!process.Responding) return null;

            if (!GetClientRect(hwnd, out var client)) return null;
            var width = client.Right - client.Left;
            var height = client.Bottom - client.Top;
            if (width < 16 || height < 16) return TryGetWindowSize(hwnd, out width, out height) ? CaptureRaw(hwnd, width, height, targetWidth) : null;

            return CaptureRaw(hwnd, width, height, targetWidth);
        }
        catch
        {
            return null;
        }
    }

    private static WindowThumbnail? CaptureRaw(nint hwnd, int width, int height, int targetWidth)
    {
        var windowDc = GetWindowDC(hwnd);
        if (windowDc == IntPtr.Zero) return null;

        var memoryDc = CreateCompatibleDC(windowDc);
        if (memoryDc == IntPtr.Zero)
        {
            ReleaseDC(hwnd, windowDc);
            return null;
        }

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = 40,
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = width * height * 4,
            },
        };

        var dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(hwnd, windowDc);
            return null;
        }

        var previous = SelectObject(memoryDc, dib);
        try
        {
            if (!PrintWindow(hwnd, memoryDc, PwRenderFullContent))
                return null;

            var stride = width * 4;
            var source = new byte[stride * height];
            for (var y = 0; y < height; y++)
                Marshal.Copy(bits + (y * stride), source, y * stride, stride);

            if (IsBlank(source, stride, width, height)) return null;

            var scale = Math.Max(1, (int)Math.Ceiling((double)width / targetWidth));
            var outWidth = width / scale;
            var outHeight = height / scale;
            if (outWidth < 2 || outHeight < 2) return null;

            var strideOut = outWidth * 4;
            var result = new byte[strideOut * outHeight];
            for (var y = 0; y < outHeight; y++)
            {
                var sourceRow = y * scale * stride;
                for (var x = 0; x < outWidth; x++)
                {
                    var src = sourceRow + (x * scale * 4);
                    var dst = (y * strideOut) + (x * 4);
                    result[dst] = source[src];
                    result[dst + 1] = source[src + 1];
                    result[dst + 2] = source[src + 2];
                    result[dst + 3] = 255;
                }
            }

            return new WindowThumbnail(result, outWidth, outHeight);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(hwnd, windowDc);
        }
    }

    private static bool IsBlank(byte[] pixels, int stride, int width, int height)
    {
        long sum = 0;
        long samples = 0;
        for (var y = 0; y < height; y += 8)
        {
            for (var x = 0; x < width; x += 8)
            {
                var offset = (y * stride) + (x * 4);
                sum += pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
                samples++;
            }
        }

        return samples == 0 || sum / samples < 4;
    }

    private static string GetClassNameOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }
}
