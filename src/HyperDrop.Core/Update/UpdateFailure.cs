namespace HyperDrop.Core.Update;

/// <summary>
/// Why an update could not be checked, downloaded or applied. The UI branches on this to decide
/// whether to offer a retry or fall back to the Releases page.
/// </summary>
public enum UpdateFailure
{
    /// <summary>GitHub could not be reached, or answered with something unusable.</summary>
    Network,

    /// <summary>The release exists but publishes no archive, or no checksum to verify one against.</summary>
    NoPackage,

    /// <summary>The download did not match the published SHA256 and was discarded.</summary>
    ChecksumMismatch,

    /// <summary>
    /// The folder HyperDrop runs from cannot be written to, so the executable cannot be replaced.
    /// </summary>
    DestinationNotWritable,

    /// <summary>Replacing the files failed and the previous version was put back.</summary>
    SwapFailed,

    /// <summary>The new version was installed but could not be started.</summary>
    LaunchFailed,
}

/// <summary>
/// User-facing text for <see cref="UpdateFailure"/>, kept beside the enum the way
/// <see cref="HyperV.HyperVErrorMessages"/> sits beside the Hyper-V return codes.
/// </summary>
public static class UpdateFailures
{
    public static string MessageFor(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Network => "HyperDrop could not reach GitHub to check for updates.",
        UpdateFailure.NoPackage => "The latest release does not publish a download HyperDrop can install.",
        UpdateFailure.ChecksumMismatch => "The download did not match its published checksum, so it was discarded.",
        UpdateFailure.DestinationNotWritable => "HyperDrop cannot write to the folder it is running from.",
        UpdateFailure.SwapFailed => "The update could not be applied. The previous version is still in place.",
        UpdateFailure.LaunchFailed => "The update was installed but the new version could not be started.",
        _ => "The update failed.",
    };

    public static string? RemedyFor(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Network => "Check your connection and try again.",
        UpdateFailure.NoPackage => "Download it from the Releases page instead.",
        UpdateFailure.ChecksumMismatch => "Try again, or download it from the Releases page.",

        // Deliberately not "run as administrator": elevating HyperDrop breaks drag & drop, so
        // moving the app somewhere writable is the fix that leaves it working.
        UpdateFailure.DestinationNotWritable =>
            "Move HyperDrop to a folder you own, such as your Desktop, or update it manually from the Releases page.",

        UpdateFailure.SwapFailed => "Close any other copy of HyperDrop and try again.",
        UpdateFailure.LaunchFailed => "Start HyperDrop again from its folder.",
        _ => null,
    };
}
