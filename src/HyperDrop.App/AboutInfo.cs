using System.Reflection;
using System.Runtime.InteropServices;
using HyperDrop.App.Interop;
using HyperDrop.Core.HyperV;

namespace HyperDrop.App;

/// <summary>
/// Product and host facts shown in the About dialog.
/// </summary>
internal static class AboutInfo
{
    public const string ProductName = "HyperDrop";

    public const string Tagline =
        "Drag files and folders straight into a running Hyper-V virtual machine, " +
        "with real progress and a notification when it finishes.";

    public const string ProjectUrl = "https://github.com/Structed/hyperdrop";

    public const string ProjectDisplayUrl = "github.com/Structed/hyperdrop";

    public const string Author = "Johannes Ebner";

    public const string Credit = $"Made by {Author} with AI.";

    public static string Version { get; } = ReadVersion();

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string OsDescription => RuntimeInformation.OSDescription;

    /// <summary>
    /// Bitness and elevation. Elevation matters because it decides which drag &amp; drop protocol
    /// the window can use, which is the single most common thing to go wrong.
    /// </summary>
    public static string ProcessDescription =>
        $"{(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, " +
        $"{(HostEnvironment.IsElevated() ? "elevated" : "not elevated")}";

    /// <summary>How the main window is currently able to receive dropped files.</summary>
    public static FileDropMode DropMode { get; set; } = FileDropMode.Ole;

    private static string DropModeDescription => DropMode switch
    {
        FileDropMode.Ole => "OLE (full)",
        FileDropMode.Legacy => "legacy WM_DROPFILES (no drag-over highlight)",
        _ => "unavailable",
    };

    /// <summary>
    /// The same facts as one block of text, so they can be pasted into a bug report.
    /// </summary>
    public static string Diagnostics() =>
        string.Join(
            Environment.NewLine,
            $"{ProductName} {Version}",
            $"Runtime: {Runtime}",
            $"OS: {OsDescription}",
            $"Process: {ProcessDescription}",
            $"Drag & drop: {DropModeDescription}");

    /// <remarks>
    /// The informational version carries the source revision after a '+' once a build has
    /// SourceLink metadata, which is noise in a dialog, so anything from there on is dropped.
    /// </remarks>
    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var revision = informational.IndexOf('+', StringComparison.Ordinal);
            return revision >= 0 ? informational[..revision] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
