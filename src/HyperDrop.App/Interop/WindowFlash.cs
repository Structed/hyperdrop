using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HyperDrop.App.Interop;

/// <summary>
/// Flashes the taskbar button so a finished transfer is noticed when the window is in the
/// background.
/// </summary>
internal static partial class WindowFlash
{
    /// <summary>Flash both the caption and the taskbar button.</summary>
    private const uint FlashwAll = 0x00000003;

    /// <summary>Keep flashing until the window comes to the foreground.</summary>
    private const uint FlashwTimerNoFg = 0x0000000C;

    internal static void Flash(Window window, uint count = 3)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.IsActive)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Handle = handle,
            Flags = FlashwAll | FlashwTimerNoFg,
            Count = count,
            Timeout = 0,
        };

        FlashWindowEx(ref info);
    }

    [LibraryImport("user32.dll", EntryPoint = "FlashWindowEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Handle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}
