namespace HyperDrop.App.Interop;

/// <summary>
/// How the window is able to receive dropped files.
/// </summary>
public enum FileDropMode
{
    /// <summary>
    /// OLE drag &amp; drop, through WPF's own <see cref="System.Windows.UIElement.AllowDrop"/>.
    /// The full experience: hover feedback, drag-over highlight, copy cursor.
    /// </summary>
    Ole,

    /// <summary>
    /// The legacy <c>WM_DROPFILES</c> protocol, used when the process is elevated and Windows
    /// blocks OLE drops. Files still arrive, but there is no hover feedback.
    /// </summary>
    Legacy,

    /// <summary>Nothing could be wired up. Only the browse buttons and paste will work.</summary>
    Unavailable,
}
