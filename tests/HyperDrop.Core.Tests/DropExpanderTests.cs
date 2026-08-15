using HyperDrop.Core.Tests.Fakes;
using HyperDrop.Core.Transfer;

namespace HyperDrop.Core.Tests;

public sealed class DropExpanderTests
{
    [Fact]
    public void Expand_SingleFile_UsesFileNameAsRelativePath()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("report.pdf", sizeBytes: 42);

        var result = DropExpander.Expand([file]);

        var item = Assert.Single(result.Items);
        Assert.Equal("report.pdf", item.RelativePath);
        Assert.Equal(file, item.SourcePath);
        Assert.Equal(42, item.SizeBytes);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Expand_Folder_RecreatesStructureUnderTheFolderName()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("payload", "root.txt"));
        temp.CreateFile(Path.Combine("payload", "docs", "guide.md"));
        temp.CreateFile(Path.Combine("payload", "docs", "deep", "notes.txt"));

        var result = DropExpander.Expand([Path.Combine(temp.Path, "payload")]);

        var relativePaths = result.Items.Select(item => item.RelativePath).OrderBy(path => path).ToList();

        Assert.Equal(
            [
                Path.Combine("payload", "docs", "deep", "notes.txt"),
                Path.Combine("payload", "docs", "guide.md"),
                Path.Combine("payload", "root.txt"),
            ],
            relativePaths);
    }

    [Fact]
    public void Expand_TrailingSeparatorOnFolder_StillUsesFolderName()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("payload", "root.txt"));

        var result = DropExpander.Expand([Path.Combine(temp.Path, "payload") + "\\"]);

        var item = Assert.Single(result.Items);
        Assert.Equal(Path.Combine("payload", "root.txt"), item.RelativePath);
    }

    [Fact]
    public void Expand_SameFileTwice_IsDeduplicated()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("once.bin");

        var result = DropExpander.Expand([file, file]);

        Assert.Single(result.Items);
    }

    [Fact]
    public void Expand_MissingPath_IsReportedAsAProblemRatherThanThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "hyperdrop-not-here", "ghost.txt");

        var result = DropExpander.Expand([missing]);

        Assert.Empty(result.Items);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(missing, problem.Path);
    }

    [Fact]
    public void Expand_EmptyFolder_ProducesNoItemsAndNoProblems()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateFolder("empty");

        var result = DropExpander.Expand([folder]);

        Assert.Empty(result.Items);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Expand_TotalBytes_SumsEveryFile()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("data", "a.bin"), sizeBytes: 100);
        temp.CreateFile(Path.Combine("data", "b.bin"), sizeBytes: 250);

        var result = DropExpander.Expand([Path.Combine(temp.Path, "data")]);

        Assert.Equal(350, result.TotalBytes);
    }

    [Fact]
    public void Expand_BlankAndWhitespaceEntries_AreIgnored()
    {
        using var temp = new TempDirectory();
        var file = temp.CreateFile("real.txt");

        var result = DropExpander.Expand([string.Empty, "   ", file]);

        Assert.Single(result.Items);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Expand_DirectorySymlink_IsSkippedToAvoidCycles()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("target", "inside.txt"));

        var linkPath = Path.Combine(temp.Path, "link");

        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(temp.Path, "target"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating symlinks needs Developer Mode or elevation; skip where unavailable.
            return;
        }

        var result = DropExpander.Expand([linkPath]);

        Assert.Empty(result.Items);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Expand_FollowingReparsePoints_WalksIntoTheLink()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(Path.Combine("target", "inside.txt"));

        var linkPath = Path.Combine(temp.Path, "link");

        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(temp.Path, "target"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var result = DropExpander.Expand(
            [linkPath],
            new DropExpanderOptions { FollowReparsePoints = true });

        Assert.Single(result.Items);
    }
}
