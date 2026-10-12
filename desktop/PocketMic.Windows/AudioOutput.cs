using NAudio.CoreAudioApi;
using NAudio.Wave;
using PocketMic.Core;

namespace PocketMic.Windows;

public sealed record OutputDevice(string Id, string Name)
{
    public bool IsVirtual => Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

public sealed class AudioOutput : IDisposable, ISampleProvider
{
    private readonly PcmBuffer buffer;
    private WasapiOut? output;
    private MMDevice? device;
    public event Action<string>? Failed;
    public float Gain { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Running => output?.PlaybackState == PlaybackState.Playing;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    public AudioOutput(PcmBuffer buffer) => this.buffer = buffer;
    public int Read(float[] samples, int offset, int count) => buffer.Read(samples, offset, count, Gain, Muted);

    public static List<OutputDevice> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<OutputDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint) result.Add(new OutputDevice(endpoint.ID, endpoint.FriendlyName));
        }
        return result.OrderByDescending(d => d.IsVirtual).ToList();
    }

    public void Start(string id)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            device = enumerator.GetDevice(id);
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
            output.PlaybackStopped += PlaybackStopped;
            output.Init(this);
            output.Play();
        }
        catch { Stop(); throw; }
    }

    private void PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Failed?.Invoke($"输出设备不可用：{e.Exception.Message}");
    }

    public void Stop()
    {
        if (output != null) { output.PlaybackStopped -= PlaybackStopped; output.Stop(); output.Dispose(); output = null; }
        device?.Dispose();
        device = null;
    }
    public void Dispose() => Stop();
}
