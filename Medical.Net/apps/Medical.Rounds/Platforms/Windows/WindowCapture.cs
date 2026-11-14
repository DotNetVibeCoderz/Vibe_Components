using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Medical.Rounds;

/// <summary>
/// Captures the whole app window with PrintWindow(PW_RENDERFULLCONTENT): unlike render-to-bitmap it includes native
/// text boxes, pickers and the navigation bar.
/// </summary>
internal static class WindowCapture
{
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out Rect r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint bmp, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant;
    }

    public static async Task SaveAsync(Window window, string path)
    {
        var native = (Microsoft.UI.Xaml.Window)window.Handler!.PlatformView!;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
        GetClientRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        nint screen = GetDC(hwnd), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        PrintWindow(hwnd, mem, 0x1 | 0x2);   // client area, full content
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        var pixels = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(hwnd, screen);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;   // opaque
        using var stream = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, pixels);
        await enc.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using (var reader = new DataReader(stream)) { await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(bytes); }
        await File.WriteAllBytesAsync(path, bytes);
    }
}
