using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PocketMic.Core;

namespace PocketMic.Windows;

public sealed class IdentityStore : IDisposable
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketMic");
    private static readonly string FilePath = Path.Combine(Folder, "identity.dpapi");
    public X509Certificate2 Certificate { get; }
    public string Token { get; private set; }
    public string Fingerprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    private sealed record Stored(string Pfx, string Token);

    public IdentityStore()
    {
        Directory.CreateDirectory(Folder);
        if (File.Exists(FilePath))
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var saved = JsonSerializer.Deserialize<Stored>(bytes) ?? throw new InvalidDataException("配对身份文件损坏。");
            Certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(saved.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            Token = saved.Token;
            if (!Pairing.ValidToken(Token)) throw new InvalidDataException("配对密钥格式错误。");
            if (Certificate.NotAfter > DateTime.Now.AddDays(7)) return;
            Certificate.Dispose();
        }
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PocketMic local receiver", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        // Windows Schannel cannot use ephemeral private keys for server TLS.
        Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        Token = Pairing.NewToken();
        Save();
    }

    public void Rotate() { Token = Pairing.NewToken(); Save(); }
    private void Save()
    {
        var saved = new Stored(Convert.ToBase64String(Certificate.Export(X509ContentType.Pfx)), Token);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(saved), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath + ".tmp", bytes);
        File.Move(FilePath + ".tmp", FilePath, true);
    }
    public void Dispose() => Certificate.Dispose();

    public static List<string> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 2)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => Pairing.IsPrivateOrLoopback(a.Address) && !System.Net.IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString()).Distinct().ToList();
}
