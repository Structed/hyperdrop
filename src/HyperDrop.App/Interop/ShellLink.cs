using System.ComponentModel;
using System.Diagnostics;
using HyperDrop.Core.HyperV;

namespace HyperDrop.App.Interop;

/// <summary>
/// Opens external links without handing them this process's token.
/// </summary>
/// <remarks>
/// HyperDrop normally runs unelevated, where a URL can simply be handed to the shell. When it has
/// been started elevated, opening the URL directly would give the default browser an elevated
/// token, so the link goes to Explorer instead: Explorer routes it through the shell that is
/// already running, which opens the browser at the user's own integrity level.
/// </remarks>
internal static class ShellLink
{
    internal static bool Open(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var info = new ProcessStartInfo(HostEnvironment.IsElevated() ? "explorer.exe" : url)
        {
            UseShellExecute = true,
        };

        if (HostEnvironment.IsElevated())
        {
            info.ArgumentList.Add(url);
        }

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
