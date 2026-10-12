using PocketMic.Windows;
using System.Security.Cryptography;
using System.Text.Json;

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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateReplacesTheExeAndRestoresItIfLaunchFails(bool failLaunch)
    {
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketMic", "updates", "test-" + Guid.NewGuid().ToString("N"));
        var targetDir = Path.Combine(Path.GetTempPath(), "PocketMic-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); Directory.CreateDirectory(targetDir);
        string source = Path.Combine(stage, "PocketMic.exe"), target = Path.Combine(targetDir, "PocketMic.exe"), planFile = Path.Combine(stage, "plan.json");
        byte[] before = [1,2,3,4], after = [5,6,7,8]; int launches = 0;
        try
        {
            await File.WriteAllBytesAsync(source, after); await File.WriteAllBytesAsync(target, before);
            var plan = new UpdatePlan(int.MaxValue, 0, target, Convert.ToHexString(SHA256.HashData(before)), source, Convert.ToHexString(SHA256.HashData(after)));
            await File.WriteAllTextAsync(planFile, JsonSerializer.Serialize(plan));
            void Launch(string path) { Assert.Equal(target, path); if (++launches == 1 && failLaunch) throw new IOException("Simulated launch failure"); }
            if (failLaunch) await Assert.ThrowsAsync<IOException>(() => UpdateService.ApplyAsync(planFile, Launch));
            else await UpdateService.ApplyAsync(planFile, Launch);
            Assert.Equal(failLaunch ? before : after, await File.ReadAllBytesAsync(target));
            Assert.Equal(before, await File.ReadAllBytesAsync(target + ".previous"));
            Assert.Equal(failLaunch ? 2 : 1, launches);
        }
        finally
        {
            foreach (var file in new[] { source, target, target + ".previous", target + ".next", planFile }) File.Delete(file);
            Directory.Delete(stage); Directory.Delete(targetDir);
        }
    }
}
