using System.Reflection;
using System.Runtime.InteropServices;
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
    /// Bitness and elevation, which decide whether Hyper-V will talk to this process at all.
    /// </summary>
    public static string ProcessDescription =>
        $"{(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, " +
        $"{(HostEnvironment.IsElevated() ? "elevated" : "not elevated")}";

    /// <summary>
    /// The same facts as one block of text, so they can be pasted into a bug report.
    /// </summary>
    public static string Diagnostics() =>
        string.Join(
            Environment.NewLine,
            $"{ProductName} {Version}",
            $"Runtime: {Runtime}",
            $"OS: {OsDescription}",
            $"Process: {ProcessDescription}");

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
