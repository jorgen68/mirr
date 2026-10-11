using System.Runtime.InteropServices;

namespace mirr.Interop;

internal static class DwmApi
{
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaSystemBackdropType = 38;

    internal const int DwmsbtMainwindow = 2;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;

        public static Margins Extended => new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
    }
}
