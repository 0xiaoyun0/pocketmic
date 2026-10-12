using PocketMic.Windows;

namespace PocketMic.Windows.Tests;
public class InstallerTests
{
    [Fact] public void InvalidBinaryIsNotTrustedByWindows()
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, "Not a signed executable"); Assert.False(Authenticode.Verify(path)); }
        finally { File.Delete(path); }
    }
    [Fact] public async Task UpdaterRejectsPlansOutsideItsOwnStagingDirectory()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.ApplyAsync(Path.Combine(Path.GetTempPath(), "hostile-plan.json")));
    }
    [Fact, Trait("Category", "Network")]
    public async Task OfficialDriverDownloadPassesHashAndAuthenticodeChecks()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var path = await DriverInstaller.PrepareAsync(new Progress<string>(), timeout.Token);
        Assert.True(File.Exists(path)); Assert.True(Authenticode.Verify(path));
        // Preparation only: this test NEVER executes the installer or changes drivers.
    }
}
