using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PocketMic.Core;

public sealed class Receiver : IAsyncDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly Func<string> token;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim slots = new(8);
    private readonly object gate = new();
    private readonly HashSet<Task> clients = [];
    private CancellationTokenSource? active;
    private Task? accepting;
    public event Action<string?>? ConnectionChanged;
    public event Action<string>? Error;
    public PcmBuffer Buffer { get; } = new();
    public string? DeviceName { get; private set; }
    public long Frames { get; private set; }
    public long RoundTripMs { get; private set; }
    public string? LastTransportError { get; private set; }
    public bool Connected => DeviceName != null;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public Receiver(X509Certificate2 certificate, Func<string> token, int port = 47655)
    {
        this.certificate = certificate;
        this.token = token;
        listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        listener.Start(8);
        accepting = AcceptLoop();
    }

    public void Disconnect() { lock (gate) active?.Cancel(); }

    private async Task AcceptLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (!Pairing.IsPrivateOrLoopback(((IPEndPoint)tcp.Client.RemoteEndPoint!).Address) || !slots.Wait(0)) { tcp.Dispose(); continue; }
                var task = HandleClient(tcp);
                lock (gate) clients.Add(task);
                _ = task.ContinueWith(t => { lock (gate) clients.Remove(t); slots.Release(); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) when (!lifetime.IsCancellationRequested) { Error?.Invoke($"监听中断：{ex.Message}"); }
    }

    private async Task HandleClient(TcpClient tcp)
    {
        using (tcp)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        using (var ssl = new SslStream(tcp.GetStream(), false))
        {
            bool ownsSession = false;
            try
            {
                tcp.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false
                }, timeout.Token);
                var first = await Protocol.ReadAsync(ssl, timeout.Token);
                if (first?.Type != PacketType.Hello) return;
                var hello = JsonSerializer.Deserialize<Hello>(first.Payload, Protocol.Json);
                if (hello == null || !Pairing.TokenEquals(token(), hello.Token) || !Protocol.ValidFormat(hello)
                    || string.IsNullOrWhiteSpace(hello.DeviceName) || hello.DeviceName.Length > 100)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Error, Protocol.Encode(new { message = "配对失效，请重新扫描电脑二维码。" }), cancel: timeout.Token);
                    return;
                }
                lock (gate)
                {
                    if (active == null && Pairing.TokenEquals(token(), hello.Token)) { active = session; ownsSession = true; }
                }
                if (!ownsSession)
                {
                    await Protocol.WriteAsync(ssl, PacketType.Error, Protocol.Encode(new { message = "电脑已连接另一台手机，请先断开。" }), cancel: timeout.Token);
                    return;
                }
                Buffer.Clear();
                Frames = RoundTripMs = 0;
                DeviceName = hello.DeviceName;
                await Protocol.WriteAsync(ssl, PacketType.Accept, Protocol.Encode(new { sampleRate = Protocol.SampleRate }), cancel: timeout.Token);
                ConnectionChanged?.Invoke(DeviceName);
                uint? previous = null;
                while (!session.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(10));
                    var packet = await Protocol.ReadAsync(ssl, idle.Token);
                    if (packet == null || packet.Type == PacketType.Stop) break;
                    if (packet.Type == PacketType.Audio)
                    {
                        if (previous.HasValue && packet.Sequence != unchecked(previous.Value + 1)) throw new InvalidDataException("Audio sequence mismatch.");
                        previous = packet.Sequence;
                        Buffer.Add(packet.Payload);
                        Frames++;
                        if (Frames % 50 == 1)
                        {
                            var stamp = new byte[8];
                            BinaryPrimitives.WriteInt64BigEndian(stamp, Environment.TickCount64);
                            await Protocol.WriteAsync(ssl, PacketType.Ping, stamp, cancel: idle.Token);
                        }
                    }
                    else if (packet.Type == PacketType.Pong && packet.Payload.Length == 8)
                    {
                        var elapsed = Environment.TickCount64 - BinaryPrimitives.ReadInt64BigEndian(packet.Payload);
                        if (elapsed is >= 0 and < 10000) RoundTripMs = elapsed;
                    }
                    else throw new InvalidDataException("Unexpected packet.");
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or JsonException or ArgumentException)
            {
                LastTransportError = ex.Message;
                // Invalid unauthenticated clients never change the visible session state.
                if (ownsSession && ex is not OperationCanceledException) Error?.Invoke("连接已中断，等待手机自动重连。");
            }
            finally
            {
                if (ownsSession)
                {
                    // Publish cleanup before releasing ownership, so the next session cannot be cleared by this one.
                    Buffer.Clear();
                    DeviceName = null;
                    Frames = RoundTripMs = 0;
                    ConnectionChanged?.Invoke(null);
                    lock (gate) { if (ReferenceEquals(active, session)) active = null; }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        if (accepting != null) await accepting;
        Task[] pending;
        lock (gate) pending = clients.ToArray();
        await Task.WhenAll(pending);
        lifetime.Dispose();
    }
}
