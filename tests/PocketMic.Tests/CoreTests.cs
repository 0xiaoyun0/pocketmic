using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PocketMic.Core;

namespace PocketMic.Tests;

public class CoreTests
{
    [Fact]
    public void PairingRoundTripPreservesChineseNameAndPin()
    {
        var pair = new Pairing("192.168.1.50", 47655, Pairing.NewToken(), new string('A', 64), "小云的电脑 & PC");
        Assert.Equal(pair, Pairing.Parse(pair.ToUri()));
    }
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.500.1")]
    [InlineData("example.com")]
    [InlineData("0.0.0.0")]
    public void PairingRejectsPublicAndMalformedHosts(string host)
    {
        Assert.Throws<FormatException>(() => Pairing.Parse(new Pairing(host, 47655, Pairing.NewToken(), new string('A', 64), "PC").ToUri()));
    }
    [Theory]
    [InlineData("10.0.0.2")]
    [InlineData("172.16.0.4")]
    [InlineData("127.0.0.1")]
    public void PairingSupportsPrivateAndUsbRoutes(string host)
    {
        var pair = new Pairing(host, 47655, Pairing.NewToken(), new string('A', 64), "PC");
        Assert.Equal(host, Pairing.Parse(pair.ToUri()).Host);
    }
    [Fact]
    public void TokensRejectMissingInvalidAndOldSecrets()
    {
        var token = Pairing.NewToken();
        Assert.True(Pairing.TokenEquals(token, token.ToLowerInvariant()));
        Assert.False(Pairing.TokenEquals(token, null)); Assert.False(Pairing.TokenEquals(token, "?"));
        Assert.False(Pairing.TokenEquals(token, Pairing.NewToken()));
    }

    [Fact]
    public async Task ProtocolHandlesFragmentedTcpReadsAndEof()
    {
        var memory = new MemoryStream(); var pcm = ToneFrame();
        await Protocol.WriteAsync(memory, PacketType.Audio, pcm, 123);
        memory.Position = 0;
        using var fragment = new FragmentStream(memory.ToArray());
        var packet = await Protocol.ReadAsync(fragment);
        Assert.Equal((uint)123, packet!.Sequence); Assert.Equal(pcm, packet.Payload);
        Assert.Null(await Protocol.ReadAsync(fragment));
    }
    [Fact]
    public async Task ProtocolRejectsOversizeBeforeAllocating()
    {
        byte[] header = [80, 77, 73, 67, 1, 1, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Protocol.ReadAsync(new MemoryStream(header)));
    }
    [Fact]
    public async Task TruncatedPayloadDoesNotBecomeAnAudioFrame()
    {
        var stream = new MemoryStream(); await Protocol.WriteAsync(stream, PacketType.Audio, ToneFrame());
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await Protocol.ReadAsync(new MemoryStream(stream.ToArray()[..^1])));
    }
    [Fact]
    public void BufferPrimesAndConvertsSignedPcm()
    {
        var buffer = new PcmBuffer { TargetMs = 40 }; var output = new float[960];
        buffer.Add(ToneFrame()); buffer.Read(output, 0, output.Length);
        Assert.All(output, v => Assert.Equal(0, v)); Assert.Equal(20, buffer.BufferedMs);
        buffer.Add(ToneFrame()); buffer.Read(output, 0, output.Length);
        Assert.Equal(.5f, output[0]); Assert.Equal(-.5f, output[1]); Assert.Equal(20, buffer.BufferedMs);
    }
    [Fact]
    public void BufferShedsOldAudioAndNeverGrowsWithNetworkBursts()
    {
        var buffer = new PcmBuffer();
        for (int i = 0; i < 10000; i++) buffer.Add(ToneFrame());
        Assert.InRange(buffer.BufferedMs, 60, 120); Assert.True(buffer.DroppedSamples > 0);
    }
    [Fact]
    public void MuteConsumesAudioInsteadOfReplayingItAfterUnmute()
    {
        var buffer = new PcmBuffer { TargetMs = 20 }; var output = new float[960];
        buffer.Add(ToneFrame()); buffer.Read(output, 0, 960, muted: true);
        Assert.All(output, v => Assert.Equal(0, v)); Assert.Equal(0, buffer.BufferedMs);
        buffer.Read(output, 0, 960); Assert.All(output, v => Assert.Equal(0, v));
    }
    [Fact]
    public void GainClipsSafelyAndDisconnectClearsAudio()
    {
        var buffer = new PcmBuffer { TargetMs = 20 }; var output = new float[960];
        buffer.Add(ToneFrame()); buffer.Read(output, 0, 960, 5);
        Assert.Equal(1, output[0]); Assert.Equal(-1, output[1]);
        buffer.Add(ToneFrame()); buffer.Clear(); buffer.Read(output, 0, 960);
        Assert.All(output, v => Assert.Equal(0, v)); Assert.Equal(0, buffer.Peak);
    }
    [Fact]
    public async Task TlsReceiverAuthenticatesStreamsPingsAndReconnects()
    {
        using var cert = Certificate(); var token = Pairing.NewToken();
        await using var receiver = new Receiver(cert, () => token, 0); receiver.Start();
        using (var client = await Connect(receiver, cert))
        {
            await Protocol.WriteAsync(client, PacketType.Hello, Protocol.Encode(new Hello(token, "Test Android", 48000, 1, 16, 960)));
            Assert.Equal(PacketType.Accept, (await Protocol.ReadAsync(client))!.Type);
            for (uint i = 0; i < 6; i++) await Protocol.WriteAsync(client, PacketType.Audio, ToneFrame(), i);
            using var timeout = new CancellationTokenSource(5000);
            var ping = await Protocol.ReadAsync(client, timeout.Token); Assert.Equal(PacketType.Ping, ping!.Type);
            await Protocol.WriteAsync(client, PacketType.Pong, ping.Payload);
            await Until(() => receiver.Frames == 6);
            Assert.True(receiver.Connected); Assert.True(receiver.Buffer.Peak > 0); Assert.InRange(receiver.Buffer.BufferedMs, 60, 120);
        }
        await Until(() => !receiver.Connected);
        Assert.Equal(0, receiver.Buffer.BufferedMs);
        using var second = await Connect(receiver, cert);
        await Protocol.WriteAsync(second, PacketType.Hello, Protocol.Encode(new Hello(token, "Reconnected", 48000, 1, 16, 960)));
        Assert.Equal(PacketType.Accept, (await Protocol.ReadAsync(second))!.Type);
    }
    [Fact]
    public async Task ReceiverRejectsWrongTokenAndBusyClients()
    {
        using var cert = Certificate(); var token = Pairing.NewToken();
        await using var receiver = new Receiver(cert, () => token, 0); receiver.Start();
        using var invalid = await Connect(receiver, cert);
        await Protocol.WriteAsync(invalid, PacketType.Hello, Protocol.Encode(new Hello(Pairing.NewToken(), "Bad", 48000, 1, 16, 960)));
        Assert.Equal(PacketType.Error, (await Protocol.ReadAsync(invalid))!.Type); Assert.False(receiver.Connected);
        using var first = await Connect(receiver, cert);
        await Protocol.WriteAsync(first, PacketType.Hello, Protocol.Encode(new Hello(token, "First", 48000, 1, 16, 960)));
        Assert.Equal(PacketType.Accept, (await Protocol.ReadAsync(first))!.Type);
        using var busy = await Connect(receiver, cert);
        await Protocol.WriteAsync(busy, PacketType.Hello, Protocol.Encode(new Hello(token, "Other", 48000, 1, 16, 960)));
        Assert.Equal(PacketType.Error, (await Protocol.ReadAsync(busy))!.Type); Assert.Equal("First", receiver.DeviceName);
    }
    [Fact]
    public async Task KeyRotationRejectsOldPairing()
    {
        using var cert = Certificate(); var token = Pairing.NewToken(); var old = token;
        await using var receiver = new Receiver(cert, () => token, 0); receiver.Start(); token = Pairing.NewToken();
        using var client = await Connect(receiver, cert);
        await Protocol.WriteAsync(client, PacketType.Hello, Protocol.Encode(new Hello(old, "Old", 48000, 1, 16, 960)));
        Assert.Equal(PacketType.Error, (await Protocol.ReadAsync(client))!.Type);
    }
    internal static byte[] ToneFrame()
    {
        var frame = new byte[1920];
        for (int i = 0; i < 960; i++) BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * 2), (short)(i % 2 == 0 ? 16384 : -16384));
        return frame;
    }
    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        using var generated = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
    private static async Task<SslStream> Connect(Receiver receiver, X509Certificate2 certificate)
    {
        var tcp = new TcpClient(); await tcp.ConnectAsync("127.0.0.1", receiver.Port);
        var stream = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256));
        try { await stream.AuthenticateAsClientAsync("PocketMic"); return stream; }
        catch (IOException ex) { stream.Dispose(); throw new IOException("Server: " + receiver.LastTransportError, ex); }
    }
    private static async Task Until(Func<bool> check)
    {
        using var timeout = new CancellationTokenSource(5000);
        while (!check()) await Task.Delay(20, timeout.Token);
    }
    private sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
