using System.Net;
using System.Security.Cryptography;

namespace PocketMic.Core;

public sealed record Pairing(string Host, int Port, string Token, string Fingerprint, string Name)
{
    public string ToUri() => $"pocketmic://pair?v=1&host={Uri.EscapeDataString(Host)}&port={Port}&key={Token}&fp={Fingerprint}&name={Uri.EscapeDataString(Name)}";

    public static Pairing Parse(string input)
    {
        var uri = new Uri(input.Trim());
        if (uri.Scheme != "pocketmic" || uri.Host != "pair") throw new FormatException("Invalid pairing link.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? ""));
        string Get(string key) => query.TryGetValue(key, out var value) ? value : throw new FormatException($"Missing {key}.");
        var host = Get("host");
        if (Get("v") != "1" || !IPAddress.TryParse(host, out var ip) || host != ip.ToString() || !IsPrivateOrLoopback(ip)) throw new FormatException("A private IPv4 address is required.");
        if (!int.TryParse(Get("port"), out var port) || port is < 1024 or > 65535) throw new FormatException("Invalid port.");
        var token = Get("key");
        if (!ValidToken(token)) throw new FormatException("Invalid pairing key.");
        var fp = Get("fp");
        if (fp.Length != 64 || !fp.All(Uri.IsHexDigit)) throw new FormatException("Invalid certificate fingerprint.");
        var name = Get("name");
        if (name.Length > 100) throw new FormatException("Name too long.");
        return new Pairing(host, port, token, fp.ToUpperInvariant(), name);
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool ValidToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
    public static bool TokenEquals(string expected, string? supplied) => ValidToken(expected) && ValidToken(supplied)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(supplied!));

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 169 && b[1] == 254);
    }
}
