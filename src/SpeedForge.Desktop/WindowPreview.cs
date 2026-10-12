using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SpeedForge.Desktop;

internal static class WindowPreview
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

    // Used only by explicit preview / verification of this application's own window.
    internal static void Save(Window window, string path)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var bounds)) throw new IOException("Не удалось прочитать размер окна");
        nint dc = GetWindowDC(handle), memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(dc);
            bitmap = CreateCompatibleBitmap(dc, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            if (dc == 0 || memory == 0 || bitmap == 0) throw new IOException("Не удалось создать снимок окна");
            previous = SelectObject(memory, bitmap);
            if (!PrintWindow(handle, memory, 2)) throw new IOException("Не удалось получить снимок окна");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path); encoder.Save(file);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (dc != 0) ReleaseDC(handle, dc);
        }
    }
}
