using HyperVDrop.Core.Model;

namespace HyperVDrop.Core.Transfer;

/// <summary>
/// Something that was dropped but cannot be copied, reported to the user instead of failing the
/// whole batch.
/// </summary>
public sealed record DropProblem(string Path, string Reason);

/// <summary>The result of turning dropped paths into a copy plan.</summary>
public sealed record DropExpansion(
    IReadOnlyList<TransferItem> Items,
    IReadOnlyList<DropProblem> Problems)
{
    public static DropExpansion Empty { get; } = new([], []);

    public long TotalBytes => Items.Sum(item => item.SizeBytes);
}

public sealed record DropExpanderOptions
{
    /// <summary>
    /// Whether to walk into junctions and symlinks. Off by default because directory links can
    /// form cycles and would otherwise expand forever.
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>Safety net against pathological directory nesting.</summary>
    public int MaxDepth { get; init; } = 64;
}

/// <summary>
/// Expands dropped files and folders into a flat list of files, each with the path it should end
/// up at relative to the destination root inside the guest.
/// </summary>
/// <remarks>
/// Dropping <c>C:\work\report</c> produces items with relative paths like <c>report\q1\data.csv</c>,
/// so the folder structure is recreated in the guest rather than flattened.
/// </remarks>
public static class DropExpander
{
    public static DropExpansion Expand(
        IEnumerable<string> droppedPaths,
        DropExpanderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(droppedPaths);
        options ??= new DropExpanderOptions();

        var items = new List<TransferItem>();
        var problems = new List<DropProblem>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = raw.Trim().TrimEnd('\\', '/');

            try
            {
                if (File.Exists(path))
                {
                    AddFile(path, Path.GetFileName(path), items, problems, seenSources);
                }
                else if (Directory.Exists(path))
                {
                    ExpandDirectory(path, options, items, problems, seenSources);
                }
                else
                {
                    problems.Add(new DropProblem(path, "Not found."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add(new DropProblem(path, Describe(ex)));
            }
        }

        return new DropExpansion(items, problems);
    }

    private static void ExpandDirectory(
        string rootPath,
        DropExpanderOptions options,
        List<TransferItem> items,
        List<DropProblem> problems,
        HashSet<string> seenSources)
    {
        if (!options.FollowReparsePoints && IsReparsePoint(rootPath))
        {
            problems.Add(new DropProblem(rootPath, "Skipped: this folder is a shortcut or junction."));
            return;
        }

        // The dropped folder's own name becomes the first segment, so structure is preserved.
        var rootName = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(rootName))
        {
            // A drive root such as "D:\" has no name; copy its contents directly.
            rootName = string.Empty;
        }

        var pending = new Stack<(string Path, string Relative, int Depth)>();
        pending.Push((rootPath, rootName, 0));

        while (pending.Count > 0)
        {
            var (currentPath, currentRelative, depth) = pending.Pop();

            if (depth > options.MaxDepth)
            {
                problems.Add(new DropProblem(currentPath, "Skipped: folder nesting is too deep."));
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(currentPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new DropProblem(currentPath, Describe(ex)));
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var relative = currentRelative.Length == 0 ? name : Path.Combine(currentRelative, name);

                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (!options.FollowReparsePoints && IsReparsePoint(entry))
                        {
                            problems.Add(new DropProblem(entry, "Skipped: this folder is a shortcut or junction."));
                            continue;
                        }

                        pending.Push((entry, relative, depth + 1));
                    }
                    else
                    {
                        AddFile(entry, relative, items, problems, seenSources);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new DropProblem(entry, Describe(ex)));
                }
            }
        }
    }

    private static void AddFile(
        string path,
        string relativePath,
        List<TransferItem> items,
        List<DropProblem> problems,
        HashSet<string> seenSources)
    {
        var fullPath = Path.GetFullPath(path);

        if (!seenSources.Add(fullPath))
        {
            return;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            problems.Add(new DropProblem(path, "Not found."));
            return;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            problems.Add(new DropProblem(path, "Skipped: this file is a shortcut or link."));
            return;
        }

        items.Add(new TransferItem
        {
            SourcePath = fullPath,
            RelativePath = Normalize(relativePath),
            SizeBytes = info.Length,
        });
    }

    /// <summary>Guest paths are Windows paths, so relative segments always use backslashes.</summary>
    private static string Normalize(string relativePath) =>
        relativePath.Replace('/', '\\').TrimStart('\\');

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied.",
        DirectoryNotFoundException => "Not found.",
        FileNotFoundException => "Not found.",
        PathTooLongException => "The path is too long.",
        _ => ex.Message,
    };
}
