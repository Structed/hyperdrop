using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HyperDrop.Core.HyperV;

namespace HyperDrop.App.Interop;

/// <summary>
/// Makes a window accept dropped files, using whichever of the two Windows drop protocols can
/// actually reach it.
/// </summary>
/// <remarks>
/// <para>
/// WPF only speaks OLE drag &amp; drop: setting <c>AllowDrop</c> registers an <c>IDropTarget</c> on
/// the window handle. Windows User Interface Privilege Isolation blocks that protocol outright
/// when the target window sits at a higher integrity level than the dragging process, which is
/// exactly the case for an elevated app receiving a drop from ordinary Explorer. No message filter
/// lifts it — <c>ChangeWindowMessageFilterEx</c> only reaches the legacy protocol.
/// </para>
/// <para>
/// HyperDrop therefore runs unelevated, where OLE drops work normally. If it is launched elevated
/// anyway, this falls back to the legacy <c>WM_DROPFILES</c> protocol, which does survive the
/// integrity boundary once <see cref="UipiDragDrop"/> has opened the message filter. The fallback
/// carries file paths and nothing else, so the drag-over highlight is lost, but a drop still works
/// instead of being silently discarded.
/// </para>
/// <para>
/// The two protocols are mutually exclusive. While an OLE drop target is registered, the shell
/// uses it and never falls back, so the legacy path has to revoke it first.
/// </para>
/// </remarks>
internal sealed partial class WindowDropTarget
{
    private const int WmDropFiles = 0x0233;

    /// <summary>Passed as the file index to ask <c>DragQueryFile</c> how many files there are.</summary>
    private const uint DragQueryCount = 0xFFFFFFFF;

    private readonly Window _window;
    private readonly Action<IReadOnlyList<string>> _onFilesDropped;

    private WindowDropTarget(Window window, Action<IReadOnlyList<string>> onFilesDropped)
    {
        _window = window;
        _onFilesDropped = onFilesDropped;
    }

    /// <summary>
    /// Wires the window up for file drops and reports which protocol ended up being used.
    /// </summary>
    /// <param name="window">
    /// The window, which must already have a handle. Call from <c>OnSourceInitialized</c> or later.
    /// </param>
    /// <param name="onFilesDropped">
    /// Receives dropped paths from the legacy protocol. Not called in
    /// <see cref="FileDropMode.Ole"/> mode, where WPF raises its own <c>Drop</c> event instead.
    /// </param>
    internal static FileDropMode Attach(Window window, Action<IReadOnlyList<string>> onFilesDropped)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(onFilesDropped);

        if (!HostEnvironment.IsElevated())
        {
            window.AllowDrop = true;
            return FileDropMode.Ole;
        }

        return new WindowDropTarget(window, onFilesDropped).AttachLegacy();
    }

    private FileDropMode AttachLegacy()
    {
        var source = (HwndSource?)PresentationSource.FromVisual(_window);

        if (source is null || source.Handle == IntPtr.Zero)
        {
            return FileDropMode.Unavailable;
        }

        if (!UipiDragDrop.Enable(source.Handle))
        {
            return FileDropMode.Unavailable;
        }

        source.AddHook(OnWindowMessage);

        // WPF registers its OLE drop target while the visual tree loads, and any element that
        // allows drops is enough to trigger it — TextBox does so by default. Revoking has to
        // happen after that, or the shell would find the OLE target and never send WM_DROPFILES.
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                RevokeDragDrop(source.Handle);
                DragAcceptFiles(source.Handle, true);
            });

        return FileDropMode.Legacy;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDropFiles)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var paths = ReadDroppedPaths(wParam);

        if (paths.Count > 0)
        {
            // Never start the transfer inside the window procedure: it is asynchronous and would
            // pump messages back into a window that is still handling this one.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _onFilesDropped(paths));
        }

        return IntPtr.Zero;
    }

    /// <summary>Reads the file names out of an <c>HDROP</c> and releases it.</summary>
    private static List<string> ReadDroppedPaths(IntPtr drop)
    {
        var paths = new List<string>();

        try
        {
            var count = DragQueryFile(drop, DragQueryCount, IntPtr.Zero, 0);

            for (uint index = 0; index < count; index++)
            {
                // The first call returns the length excluding the terminator, so ask before
                // allocating rather than guessing at MAX_PATH: the app is long-path aware.
                var length = DragQueryFile(drop, index, IntPtr.Zero, 0);

                if (length == 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal(((int)length + 1) * sizeof(char));

                try
                {
                    var written = DragQueryFile(drop, index, buffer, length + 1);

                    if (written > 0 && Marshal.PtrToStringUni(buffer, (int)written) is { Length: > 0 } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            DragFinish(drop);
        }

        return paths;
    }

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static partial uint DragQueryFile(IntPtr drop, uint index, IntPtr buffer, uint bufferLength);

    [LibraryImport("shell32.dll")]
    private static partial void DragAcceptFiles(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [LibraryImport("shell32.dll")]
    private static partial void DragFinish(IntPtr drop);

    /// <remarks>Returns <c>DRAGDROP_E_NOTREGISTERED</c> when nothing was registered, which is fine.</remarks>
    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(IntPtr hwnd);
}
