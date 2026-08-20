using System.IO.Compression;
using System.Text;
using HyperDrop.Core.Tests.Fakes;
using HyperDrop.Core.Update;

namespace HyperDrop.Core.Tests;

public sealed class UpdateInstallerTests
{
    private const string ExecutableName = "HyperDrop.exe";

    [Fact]
    public void Apply_ReplacesTheExecutableAndKeepsThePreviousOneAside()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"));
        var package = CreatePackage(temp, ("HyperDrop.exe", "new"));

        installer.Apply(package);

        Assert.Equal("new", File.ReadAllText(executable));
        Assert.Equal("old", File.ReadAllText(executable + UpdateInstaller.BackupSuffix));
    }

    [Fact]
    public void Apply_InstallsFilesThatAreNewInThisRelease()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"));
        var package = CreatePackage(
            temp,
            ("HyperDrop.exe", "new"),
            ("runtimes/win-x64/native/extra.dll", "native"));

        installer.Apply(package);

        var added = Path.Combine(
            Path.GetDirectoryName(executable)!,
            "runtimes",
            "win-x64",
            "native",
            "extra.dll");

        Assert.Equal("native", File.ReadAllText(added));
    }

    [Fact]
    public void Apply_LeavesNoStagingFolderBehind()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"));

        installer.Apply(CreatePackage(temp, ("HyperDrop.exe", "new")));

        var folders = Directory.GetDirectories(Path.GetDirectoryName(executable)!);
        Assert.DoesNotContain(folders, folder => Path.GetFileName(folder).StartsWith('.'));
    }

    [Fact]
    public void Apply_WithAPackageThatDoesNotContainTheApp_ChangesNothing()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"));
        var package = CreatePackage(temp, ("readme.txt", "not an app"));

        var failure = Assert.Throws<UpdateException>(() => installer.Apply(package));

        Assert.Equal(UpdateFailure.SwapFailed, failure.Reason);
        Assert.Equal("old", File.ReadAllText(executable));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "readme.txt")));
    }

    [Fact]
    public void Apply_WithACorruptPackage_Fails()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"));
        var package = Path.Combine(temp.CreateFolder("downloads"), "broken.zip");
        File.WriteAllText(package, "this is not a zip");

        var failure = Assert.Throws<UpdateException>(() => installer.Apply(package));

        Assert.Equal(UpdateFailure.SwapFailed, failure.Reason);
        Assert.Equal("old", File.ReadAllText(executable));
    }

    [Fact]
    public void Apply_WhenAFileCannotBeReplaced_PutsThePreviousVersionBack()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"), ("locked.dll", "old-dll"));
        var package = CreatePackage(temp, ("HyperDrop.exe", "new"), ("locked.dll", "new-dll"));
        var locked = Path.Combine(Path.GetDirectoryName(executable)!, "locked.dll");

        // Holding the file open with no sharing makes the rename fail the way a virus scanner
        // holding a handle would.
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = Assert.Throws<UpdateException>(() => installer.Apply(package));
            Assert.Equal(UpdateFailure.SwapFailed, failure.Reason);
        }

        Assert.Equal("old", File.ReadAllText(executable));
        Assert.Equal("old-dll", File.ReadAllText(locked));
        Assert.False(File.Exists(executable + UpdateInstaller.BackupSuffix));
    }

    [Fact]
    public void Apply_IntoAFolderThatCannotBeWritten_SaysSoInsteadOfTrying()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "gone", ExecutableName);
        var installer = new UpdateInstaller(missing, (_, _) => true);
        var package = CreatePackage(temp, ("HyperDrop.exe", "new"));

        var failure = Assert.Throws<UpdateException>(() => installer.Apply(package));

        Assert.Equal(UpdateFailure.DestinationNotWritable, failure.Reason);
    }

    [Fact]
    public void CanInstall_InAFolderWeOwn_IsTrue()
    {
        using var temp = new TempDirectory();
        var (installer, _) = InstallerFor(temp, ("HyperDrop.exe", "old"));

        Assert.True(installer.CanInstall());
    }

    [Fact]
    public void CanInstall_LeavesNoProbeFileBehind()
    {
        using var temp = new TempDirectory();
        var (installer, executable) = InstallerFor(temp, ("HyperDrop.exe", "old"));

        installer.CanInstall();

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(executable)!));
    }

    [Fact]
    public void Relaunch_StartsTheInstalledExecutable()
    {
        using var temp = new TempDirectory();
        var started = new List<string>();
        var (installer, executable) = InstallerFor(
            temp,
            launcher: (path, _) =>
            {
                started.Add(path);
                return true;
            },
            files: ("HyperDrop.exe", "old"));

        installer.Relaunch();

        Assert.Equal([executable], started);
    }

    [Fact]
    public void Relaunch_WhenTheNewVersionWillNotStart_SaysSo()
    {
        using var temp = new TempDirectory();
        var (installer, _) = InstallerFor(temp, launcher: (_, _) => false, files: ("HyperDrop.exe", "old"));

        Assert.Equal(UpdateFailure.LaunchFailed, Assert.Throws<UpdateException>(installer.Relaunch).Reason);
    }

    [Fact]
    public void CleanupPreviousVersion_DeletesWhatAnEarlierUpdateLeftBehind()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateFolder("app");
        var stale = Path.Combine(folder, "HyperDrop.exe" + UpdateInstaller.BackupSuffix);
        var nested = Path.Combine(temp.CreateFolder(Path.Combine("app", "runtimes")), "extra.dll.old");
        var keep = Path.Combine(folder, "HyperDrop.exe");

        File.WriteAllText(stale, "previous");
        File.WriteAllText(nested, "previous");
        File.WriteAllText(keep, "current");

        UpdateInstaller.CleanupPreviousVersion(folder);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void CleanupPreviousVersion_OnAFolderThatIsGone_DoesNothing() =>
        UpdateInstaller.CleanupPreviousVersion(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    private static (UpdateInstaller Installer, string Executable) InstallerFor(
        TempDirectory temp,
        params (string Name, string Content)[] files) =>
        InstallerFor(temp, (_, _) => true, files);

    private static (UpdateInstaller Installer, string Executable) InstallerFor(
        TempDirectory temp,
        Func<string, string, bool> launcher,
        params (string Name, string Content)[] files)
    {
        var folder = temp.CreateFolder("app");

        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(folder, name), content);
        }

        var executable = Path.Combine(folder, ExecutableName);
        return (new UpdateInstaller(executable, launcher), executable);
    }

    /// <summary>
    /// Writes a package shaped like the one <c>release.yml</c> produces: entries at the root of the
    /// archive rather than nested under a folder.
    /// </summary>
    private static string CreatePackage(TempDirectory temp, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(temp.CreateFolder("downloads"), "HyperDrop-v2026.8.20.7-win-x64.zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return path;
    }
}
