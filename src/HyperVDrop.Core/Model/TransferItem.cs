namespace HyperVDrop.Core.Model;

/// <summary>
/// One file to copy into a guest, produced by <see cref="Transfer.DropExpander"/>.
/// </summary>
public sealed record TransferItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Absolute path on the host.</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Path relative to the destination root inside the guest, for example <c>docs\readme.md</c>.
    /// Always uses backslashes and never starts with a separator.
    /// </summary>
    public required string RelativePath { get; init; }

    public required long SizeBytes { get; init; }

    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>Combines the guest destination root with <see cref="RelativePath"/>.</summary>
    public string ResolveDestination(string destinationRoot) =>
        string.Concat(destinationRoot.TrimEnd('\\', '/'), "\\", RelativePath);
}
