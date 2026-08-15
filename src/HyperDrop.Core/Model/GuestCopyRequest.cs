namespace HyperDrop.Core.Model;

/// <summary>
/// A single host-to-guest file copy, as handed to an
/// <see cref="Abstractions.IGuestFileCopier"/>.
/// </summary>
public sealed record GuestCopyRequest
{
    public required string VmName { get; init; }

    public required string VmId { get; init; }

    /// <summary>Absolute path on the host. Must be readable by the copy engine.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Absolute path inside the guest, including the file name.</summary>
    public required string DestinationPath { get; init; }

    public required long SizeBytes { get; init; }

    public bool OverwriteExisting { get; init; }

    /// <summary>Create missing directories in the guest rather than failing.</summary>
    public bool CreateFullPath { get; init; } = true;
}
