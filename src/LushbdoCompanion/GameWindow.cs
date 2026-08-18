using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LushbdoCompanion;

/// <summary>
/// Finds Black Desert Online's window: a walk of the OS process list plus the
/// window handle Windows already exposes for it — ordinary observation, the
/// game process is never opened or touched. The visible bounds come from DWM's
/// extended frame rect, which is the same surface Windows.Graphics.Capture
/// serves for a window, so window-relative region coordinates and captured
/// pixels agree by construction.
/// </summary>
internal static class GameWindow
{
    // The retail client is BlackDesert64.exe; the long-retired 32-bit name is
    // matched too because checking costs nothing.
    private static readonly string[] ProcessNames = ["BlackDesert64", "BlackDesert32"];

    /// <summary>How the game is referred to in log messages about (not) finding it.</summary>
    public const string Description = "BlackDesert64";

    public readonly record struct Found(IntPtr Hwnd, Rectangle Bounds);

    /// <summary>The game's main window and its visible bounds in physical screen pixels, or null when the game is not up.</summary>
    public static Found? Find()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) continue;
                    var bounds = VisibleBounds(hwnd);
                    if (bounds.Width < 1 || bounds.Height < 1) continue;
                    return new Found(hwnd, bounds);
                }
            }
        }
        return null;
    }

    private static Rectangle VisibleBounds(IntPtr hwnd)
    {
        const uint DwmwaExtendedFrameBounds = 9;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(hwnd, out rect))
            return Rectangle.Empty;
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out RECT value, int size);
}
