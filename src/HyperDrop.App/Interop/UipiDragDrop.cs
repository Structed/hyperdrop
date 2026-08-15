using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HyperDrop.App.Interop;

/// <summary>
/// Allows drag &amp; drop messages from lower-integrity processes to reach this window.
/// </summary>
/// <remarks>
/// <para>
/// HyperDrop must run elevated because the Hyper-V WMI provider requires it. That puts the window
/// at high integrity, and Windows User Interface Privilege Isolation silently discards messages
/// sent to it by medium-integrity processes — including the drag &amp; drop messages Explorer
/// sends. Without this filter the app looks perfectly healthy and simply ignores every drop.
/// </para>
/// <para>
/// <c>ChangeWindowMessageFilterEx</c> opens a per-window hole for the three messages OLE drag
/// &amp; drop needs. <c>WM_COPYGLOBALDATA</c> is the undocumented but essential one: it carries the
/// actual <c>HGLOBAL</c> payload holding the dropped file names.
/// </para>
/// </remarks>
internal static partial class UipiDragDrop
{
    private const uint WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;

    private const uint MsgfltAllow = 1;

    private static readonly uint[] RequiredMessages = [WmDropFiles, WmCopyData, WmCopyGlobalData];

    /// <summary>
    /// Applies the message filter to a window. Safe to call on a non-elevated process, where it is
    /// simply a no-op that succeeds.
    /// </summary>
    /// <returns><c>true</c> when every required message was allowed through.</returns>
    internal static bool Enable(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var allowed = true;

        foreach (var message in RequiredMessages)
        {
            allowed &= ChangeWindowMessageFilterEx(handle, message, MsgfltAllow, IntPtr.Zero);
        }

        return allowed;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(
        IntPtr hwnd,
        uint message,
        uint action,
        IntPtr changeInfo);
}
