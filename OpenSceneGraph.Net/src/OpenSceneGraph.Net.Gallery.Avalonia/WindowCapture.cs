using System.Runtime.InteropServices;
using Avalonia.Controls;
using OsgImage = OpenSceneGraph.Net.State.Image;

namespace OpenSceneGraph.Net.Desktop;

/// <summary>
/// Captures a whole window (Avalonia UI + the OpenGL view) on Windows. Avalonia's RenderTargetBitmap cannot see GL
/// content, so this uses Win32 <c>PrintWindow(PW_RENDERFULLCONTENT)</c>, which asks DWM for the composed window, and
/// falls back to copying the window's screen area with <c>BitBlt</c>.
/// </summary>
public static class WindowCapture
{
    public static OsgImage? Capture(Window window)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return null;
        if (!GetClientRect(hwnd, out var rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;

        var img = Grab(hwnd, w, h, fromScreen: false);
        if (img is null || LooksBlank(img))
        {
            var screen = Grab(hwnd, w, h, fromScreen: true);
            if (screen is not null) img = screen;
        }
        return img;
    }

    private static OsgImage? Grab(nint hwnd, int w, int h, bool fromScreen)
    {
        nint screenDc = GetDC(fromScreen ? 0 : hwnd);
        nint mem = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        nint dib = CreateDIBSection(screenDc, ref bmi, 0, out nint bits, 0, 0);
        nint old = SelectObject(mem, dib);
        try
        {
            bool ok;
            if (fromScreen)
            {
                var pt = new POINT();
                ClientToScreen(hwnd, ref pt);
                ok = BitBlt(mem, 0, 0, w, h, screenDc, pt.X, pt.Y, 0x00CC0020 /* SRCCOPY */);
            }
            else ok = PrintWindow(hwnd, mem, 0x1 | 0x2 /* PW_CLIENTONLY | PW_RENDERFULLCONTENT */);
            if (!ok) return null;
            var bgra = new byte[w * h * 4];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            for (int i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                bgra[i + 3] = 255;
            }
            return new OsgImage(w, h, bgra);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(fromScreen ? 0 : hwnd, screenDc);
        }
    }

    /// <summary>True when almost every sampled pixel is pure black (capture failed).</summary>
    public static bool LooksBlank(OsgImage img)
    {
        int black = 0, total = 0;
        for (int y = 0; y < img.Height; y += 17)
            for (int x = 0; x < img.Width; x += 17)
            {
                int i = (y * img.Width + x) * 4;
                total++;
                if (img.Pixels[i] < 4 && img.Pixels[i + 1] < 4 && img.Pixels[i + 2] < 4) black++;
            }
        return total == 0 || black > total * 0.97;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT pt);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO bmi, uint usage, out nint bits, nint section, uint offset);
}
