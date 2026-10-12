using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PocketMic.Core;

public sealed record ReleaseAsset(string Name, string Sha256, long Size);
public sealed record ReleaseManifest(int Schema, string Version, ReleaseAsset Windows, ReleaseAsset Android)
{
    public const string Repository = "0xiaoyun0/pocketmic";
    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")) throw new FormatException("Invalid release version.");
        return System.Version.Parse(value.TrimStart('v'));
    }
    public void Validate(string releaseTag)
    {
        if (Schema != 1 || ParseVersion(Version) != ParseVersion(releaseTag)) throw new InvalidDataException("Release manifest does not match the release.");
        ValidateAsset(Windows, $"PocketMic-{Version}-windows-x64.zip", 200_000_000);
        ValidateAsset(Android, $"PocketMic-{Version}-android.apk", 100_000_000);
    }
    private static void ValidateAsset(ReleaseAsset asset, string expected, long maximum)
    {
        if (asset == null || asset.Name != expected || asset.Size is <= 0 || asset.Size > maximum || asset.Sha256?.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid release asset.");
    }
    public static Uri AssetUri(string version, string name)
    {
        ParseVersion(version);
        if (name != $"PocketMic-{version}-windows-x64.zip" && name != $"PocketMic-{version}-android.apk" && name != "update-manifest.json") throw new InvalidDataException("Unexpected asset name.");
        return new Uri($"https://github.com/{Repository}/releases/download/v{version}/{name}");
    }
    public static async Task VerifyFileAsync(string path, string sha256, long size, CancellationToken cancel = default)
    {
        if (new FileInfo(path).Length != size) throw new InvalidDataException("Downloaded file size mismatch.");
        using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancel);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(sha256))) throw new InvalidDataException("Downloaded file checksum mismatch.");
    }
}
