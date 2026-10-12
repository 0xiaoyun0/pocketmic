using System.Buffers.Binary;

namespace PocketMic.Core;

/// <summary>Bounded playout buffer. Re-primes after underruns and sheds stale audio after bursts.</summary>
public sealed class PcmBuffer
{
    private readonly object gate = new();
    private readonly short[] ring = new short[Protocol.SampleRate / 5];
    private int read, count;
    private bool primed;
    public int TargetMs { get; set; } = 60;
    public long DroppedSamples { get; private set; }
    public long Underruns { get; private set; }
    public float Peak { get; private set; }
    public double BufferedMs { get { lock (gate) return count * 1000.0 / Protocol.SampleRate; } }

    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length != Protocol.AudioBytes) throw new InvalidDataException("Expected a 20 ms mono frame.");
        lock (gate)
        {
            var target = Protocol.SampleRate * Math.Clamp(TargetMs, 20, 120) / 1000;
            // Above target + 60 ms, retain only recent frames. Never grow unbounded latency.
            if (count + Protocol.FrameSamples > target + 3 * Protocol.FrameSamples)
            {
                int drop = Math.Max(0, count - target);
                read = (read + drop) % ring.Length;
                count -= drop;
                DroppedSamples += drop;
            }
            float peak = 0;
            for (int i = 0; i < pcm.Length; i += 2)
            {
                short value = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
                ring[(read + count++) % ring.Length] = value;
                peak = Math.Max(peak, Math.Abs(value / 32768f));
            }
            Peak = peak;
        }
    }

    public int Read(float[] output, int offset, int requested, float gain = 1, bool muted = false)
    {
        lock (gate)
        {
            Array.Clear(output, offset, requested);
            if (!primed && count < Protocol.SampleRate * Math.Clamp(TargetMs, 20, 120) / 1000) return requested;
            primed = true;
            int available = Math.Min(requested, count);
            for (int i = 0; i < available; i++)
            {
                output[offset + i] = muted ? 0 : Math.Clamp(ring[read] / 32768f * gain, -1f, 1f);
                read = (read + 1) % ring.Length;
            }
            count -= available;
            if (available < requested) { primed = false; Underruns++; }
            return requested;
        }
    }

    public void Clear()
    {
        lock (gate) { read = count = 0; primed = false; Peak = 0; DroppedSamples = Underruns = 0; }
    }
}
