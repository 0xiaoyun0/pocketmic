using System.Security.Cryptography;
using PocketMic.Core;

namespace PocketMic.Tests;
public class UpdateTests
{
    [Fact] public void VersionsUseNumericOrdering()
    {
        Assert.True(ReleaseManifest.ParseVersion("v0.10.0") > ReleaseManifest.ParseVersion("0.9.9"));
        Assert.Equal(ReleaseManifest.ParseVersion("0.1.0"), ReleaseManifest.ParseVersion("v0.1.0"));
    }
    [Theory]
    [InlineData("v1.0.0/../../bad")][InlineData("01.2.3")][InlineData("v1.2")][InlineData("1.2.3.4")]
    public void UnsafeVersionsAreRejected(string text) => Assert.Throws<FormatException>(() => ReleaseManifest.ParseVersion(text));
    [Fact] public void ManifestMustBindTheVersionAndExpectedAssets()
    {
        var manifest = new ReleaseManifest(1, "0.2.0", new("PocketMic-0.2.0-windows-x64.zip", new string('A', 64), 10), new("PocketMic-0.2.0-android.apk", new string('A', 64), 20));
        manifest.Validate("v0.2.0");
        Assert.Throws<InvalidDataException>(() => manifest.Validate("v0.3.0"));
        Assert.Throws<InvalidDataException>(() => (manifest with { Windows = manifest.Windows with { Name = "../../PocketMic.exe" } }).Validate("v0.2.0"));
        Assert.Throws<InvalidDataException>(() => (manifest with { Android = manifest.Android with { Size = 100000001 } }).Validate("v0.2.0"));
    }
    [Fact] public void AssetUrlsCannotRedirectToAnotherRepository()
    {
        Assert.Equal("github.com", ReleaseManifest.AssetUri("0.2.0", "update-manifest.json").Host);
        Assert.Throws<InvalidDataException>(() => ReleaseManifest.AssetUri("0.2.0", "https://example.com/payload.exe"));
    }
    [Fact] public async Task ModifiedAndTruncatedDownloadsAreRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            byte[] original = [1, 2, 3, 4]; var hash = Convert.ToHexString(SHA256.HashData(original));
            await File.WriteAllBytesAsync(path, original); await ReleaseManifest.VerifyFileAsync(path, hash, 4);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 5]);
            await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseManifest.VerifyFileAsync(path, hash, 4));
            await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseManifest.VerifyFileAsync(path, hash, 5));
        }
        finally { File.Delete(path); }
    }
}
