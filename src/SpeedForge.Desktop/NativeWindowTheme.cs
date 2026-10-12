using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SpeedForge.Desktop;

internal static class NativeWindowTheme
{
    internal const int DarkMode = 20, Border = 34, Caption = 35, Text = 36;
    internal const int BorderColor = 0x473224, CaptionColor = 0x1D120B, TextColor = 0xFCF1EA;
    internal static readonly Dictionary<int, int> Applied = [];

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    internal static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int dark = 1;
        Applied[DarkMode] = DwmSetWindowAttribute(handle, DarkMode, ref dark, sizeof(int));
        if (Applied[DarkMode] < 0)
            Applied[DarkMode] = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        Set(Border, BorderColor); Set(Caption, CaptionColor); Set(Text, TextColor);
        void Set(int attribute, int color) => Applied[attribute] = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));
    }

    internal static int? Read(Window window, int attribute) =>
        DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, attribute, out int value, sizeof(int)) >= 0 ? value : null;
}
