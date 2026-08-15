using HyperVDrop.Core.Settings;
using HyperVDrop.Core.Tests.Fakes;

namespace HyperVDrop.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));

        var settings = store.Load();

        Assert.Null(settings.LastVmId);
        Assert.True(settings.CreateFullPath);
        Assert.False(settings.OverwriteExisting);
        Assert.Equal(AppSettings.FallbackDestination, settings.DestinationFor(null));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverySetting()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);

        var settings = new AppSettings
        {
            LastVmId = "vm-1",
            OverwriteExisting = true,
            CreateFullPath = false,
            NotifyOnCompletion = false,
            PlaySoundOnCompletion = false,
            StageNetworkSources = false,
            PowerShellChunkSizeBytes = 4096,
        };

        settings.SetDestination("vm-1", @"D:\Incoming");
        store.Save(settings);

        var reloaded = new SettingsStore(path).Load();

        Assert.Equal("vm-1", reloaded.LastVmId);
        Assert.True(reloaded.OverwriteExisting);
        Assert.False(reloaded.CreateFullPath);
        Assert.False(reloaded.NotifyOnCompletion);
        Assert.False(reloaded.PlaySoundOnCompletion);
        Assert.False(reloaded.StageNetworkSources);
        Assert.Equal(4096, reloaded.PowerShellChunkSizeBytes);
        Assert.Equal(@"D:\Incoming", reloaded.DestinationFor("vm-1"));
    }

    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ this is not json ");

        var settings = new SettingsStore(path).Load();

        Assert.NotNull(settings);
        Assert.Equal(AppSettings.FallbackDestination, settings.DestinationFor(null));
    }

    [Fact]
    public void Save_CreatesMissingDirectories()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "nested", "deeper", "settings.json");

        new SettingsStore(path).Save(new AppSettings { LastVmId = "vm-9" });

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");

        new SettingsStore(path).Save(new AppSettings());

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void DestinationFor_UnknownVm_UsesTheFallback()
    {
        var settings = new AppSettings();
        settings.SetDestination("vm-1", @"D:\One");

        Assert.Equal(@"D:\One", settings.DestinationFor("vm-1"));
        Assert.Equal(AppSettings.FallbackDestination, settings.DestinationFor("vm-2"));
    }

    [Fact]
    public void DestinationFor_IsCaseInsensitiveOnVmId()
    {
        var settings = new AppSettings();
        settings.SetDestination("VM-ABC", @"D:\One");

        Assert.Equal(@"D:\One", settings.DestinationFor("vm-abc"));
    }
}
