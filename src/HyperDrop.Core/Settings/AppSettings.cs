namespace HyperDrop.Core.Settings;

/// <summary>
/// User preferences, persisted between runs.
/// </summary>
public sealed class AppSettings
{
    public const string FallbackDestination = @"C:\Users\Public\Downloads";

    /// <summary>VM selected last time, restored on startup when it still exists.</summary>
    public string? LastVmId { get; set; }

    /// <summary>
    /// Guest destination folder per VM id. Different VMs usually want different targets, and
    /// retyping the path every time is the main friction in this workflow.
    /// </summary>
    public Dictionary<string, string> DestinationsByVm { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool OverwriteExisting { get; set; }

    public bool CreateFullPath { get; set; } = true;

    public bool NotifyOnCompletion { get; set; } = true;

    public bool PlaySoundOnCompletion { get; set; } = true;

    /// <summary>Copy network sources locally first so the Hyper-V service can read them.</summary>
    public bool StageNetworkSources { get; set; } = true;

    /// <summary>Chunk size used by the PowerShell Direct engine.</summary>
    public int PowerShellChunkSizeBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Look for a newer release on startup. This is the only thing HyperDrop uses the network
    /// for, so it is a single switch rather than something buried behind several.
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// When the last automatic check ran, used to hold the startup check to once a day. A manual
    /// check from the About dialog ignores it.
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// A version the user chose to skip. Only that exact version is suppressed, so the next
    /// release surfaces normally.
    /// </summary>
    public string? SkippedUpdateVersion { get; set; }

    public string DestinationFor(string? vmId) =>
        vmId is not null && DestinationsByVm.TryGetValue(vmId, out var destination) && !string.IsNullOrWhiteSpace(destination)
            ? destination
            : FallbackDestination;

    public void SetDestination(string vmId, string destination)
    {
        if (string.IsNullOrWhiteSpace(vmId))
        {
            return;
        }

        DestinationsByVm[vmId] = destination;
    }
}
