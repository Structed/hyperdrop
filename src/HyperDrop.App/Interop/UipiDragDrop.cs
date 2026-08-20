using System.Runtime.InteropServices;

namespace HyperDrop.App.Interop;

/// <summary>
/// Lets legacy <c>WM_DROPFILES</c> drop messages from lower-integrity processes reach a window.
/// </summary>
/// <remarks>
/// <para>
/// Windows User Interface Privilege Isolation discards messages sent to a high-integrity window by
/// a medium-integrity process, which is what Explorer is. <c>ChangeWindowMessageFilterEx</c> opens
/// a per-window hole for the three messages the legacy drop protocol needs.
/// <c>WM_COPYGLOBALDATA</c> is the undocumented but essential one: it carries the <c>HGLOBAL</c>
/// holding the dropped file names.
/// </para>
/// <para>
/// This is <em>only</em> useful for the legacy protocol. It is widely cited as the fix for drag
/// &amp; drop into elevated applications, but it does nothing for OLE drag &amp; drop — the
/// protocol WPF and every other modern framework actually use — because that does not travel by
/// window message at all. HyperDrop therefore runs unelevated by default, and treats this as the
/// fallback for when it is launched elevated anyway. See <see cref="WindowDropTarget"/>.
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
    /// Applies the message filter to a window handle. Harmless on an unelevated process, where
    /// there is no barrier to open in the first place.
    /// </summary>
    /// <returns><c>true</c> when every required message was allowed through.</returns>
    internal static bool Enable(IntPtr handle)
    {
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
