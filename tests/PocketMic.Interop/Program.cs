using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PocketMic.Core;

if (args.Length != 1) throw new ArgumentException("Pass the compiled Java class directory.");
using var rsa = RSA.Create(2048);
using var generated = new CertificateRequest("CN=PocketMic interop", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
var token = Pairing.NewToken();
await using var receiver = new Receiver(certificate, () => token, 0);
int connections = 0;
receiver.ConnectionChanged += name => { if (name != null) Interlocked.Increment(ref connections); };
receiver.Start();
var pairing = new Pairing("127.0.0.1", receiver.Port, token, certificate.GetCertHashString(HashAlgorithmName.SHA256), "Interop");
var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
var java = javaHome == null ? "java" : Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
using var process = new Process { StartInfo = new ProcessStartInfo(java) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
process.StartInfo.ArgumentList.Add("-cp"); process.StartInfo.ArgumentList.Add(args[0]); process.StartInfo.ArgumentList.Add("InteropClient");
process.Start();
await process.StandardInput.WriteLineAsync(pairing.ToUri()); process.StandardInput.Close();
var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
bool sawAudio = false, sawDrop = false;
double maxBuffered = 0;
try
{
    var samples = new float[960];
    while (!process.HasExited)
    {
        maxBuffered = Math.Max(maxBuffered, receiver.Buffer.BufferedMs);
        sawDrop |= receiver.Buffer.DroppedSamples > 0;
        receiver.Buffer.Read(samples, 0, samples.Length);
        sawAudio |= samples.Any(s => Math.Abs(s) > .1);
        await Task.Delay(20, timeout.Token);
    }
    await process.WaitForExitAsync(timeout.Token);
    Console.Write(await stdout);
    if (process.ExitCode != 0) throw new Exception(await stderr);
    if (!sawAudio || !sawDrop || connections != 2 || maxBuffered > 120) throw new Exception($"Unexpected audio result: audible={sawAudio}, shedBurst={sawDrop}, sessions={connections}, maxBuffer={maxBuffered}.");
    Console.WriteLine($"PASS: receiver decoded actual Java PCM; {connections} sessions; max sampled buffer {maxBuffered:0} ms; stale audio shed.");
}
finally { if (!process.HasExited) process.Kill(true); }
