using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DS3Minimap.App;

internal static partial class GameWindowTracker
{
    internal static GameWindowState? TryGet()
    {
        try
        {
            using var process = Process.GetProcessesByName("DarkSoulsIII").FirstOrDefault();
            if (process is null || process.HasExited || process.MainWindowHandle == 0) return null;
            var handle = process.MainWindowHandle;
            if (!GetClientRect(handle, out var rect)) return null;
            var origin = new Point();
            if (!ClientToScreen(handle, ref origin)) return null;
            var focused = GetForegroundWindow() == handle;
            return new GameWindowState(handle, origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top, focused, IsIconic(handle));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process can disappear between enumeration, HasExited and window
            // inspection. Treat that race exactly like a closed game.
            return null;
        }
    }

    internal static void ApplyBorderless(nint window)
    {
        const long caption = 0x00C00000L;
        const long thickFrame = 0x00040000L;
        const long minMaxBoxes = 0x00030000L;
        var style = GetWindowLongPtr(window, -16).ToInt64();
        if ((style & (caption | thickFrame | minMaxBoxes)) == 0) return;
        SetWindowLongPtr(window, -16, new nint(style & ~(caption | thickFrame | minMaxBoxes)));
        SetWindowPos(window, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtr(nint window, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial nint SetWindowLongPtr(nint window, int index, nint style);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
}

internal sealed record GameWindowState(nint Handle, int X, int Y, int Width, int Height, bool Focused, bool Minimized);
