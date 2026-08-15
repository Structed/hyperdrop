namespace HyperDrop.Core.Tests.Fakes;

/// <summary>
/// A scratch directory that deletes itself at the end of a test.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hyperdrop-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string relativePath, int sizeBytes = 16)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        var folder = System.IO.Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(fullPath, new byte[sizeBytes]);
        return fullPath;
    }

    public string CreateFolder(string relativePath)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder must not fail a test run.
        }
    }
}
