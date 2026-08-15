using System.ComponentModel;
using System.Diagnostics;

namespace HyperDrop.App.Interop;

/// <summary>
/// Opens external links from this elevated process.
/// </summary>
/// <remarks>
/// Launching a URL directly would hand the default browser this process's elevated token, so the
/// link is handed to Explorer instead. Explorer routes it through the shell that is already
/// running, which opens the browser at the user's own integrity level.
/// </remarks>
internal static class ShellLink
{
    internal static bool Open(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(url);

        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
