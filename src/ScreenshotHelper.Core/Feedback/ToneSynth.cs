using System.Buffers.Binary;

namespace ScreenshotHelper.Core.Feedback;

/// <summary>Oscillator shape; different shapes give event families distinct timbres.</summary>
public enum Waveform
{
    Sine,
    Triangle,
    Square,
}

/// <summary>One note of a feedback sound. A frequency of 0 is silence; <paramref name="EndFrequency"/> makes a sweep.</summary>
public sealed record Tone(double Frequency, int DurationMs, Waveform Waveform = Waveform.Sine, double Gain = 1.0, double? EndFrequency = null);

/// <summary>Renders tone sequences to in-memory 16-bit mono PCM WAV files.</summary>
public static class ToneSynth
{
    public const int SampleRate = 44_100;

    // Short fades stop the audible click a hard start/stop of a waveform makes.
    private const double FadeMs = 5;

    public static byte[] Render(IReadOnlyList<Tone> tones, double volume)
    {
        ArgumentNullException.ThrowIfNull(tones);
        volume = Math.Clamp(volume, 0, 1);
        var totalSamples = tones.Sum(t => SamplesFor(t.DurationMs));
        var wav = new byte[44 + (totalSamples * 2)];
        WriteHeader(wav, totalSamples);

        var offset = 44;
        foreach (var tone in tones)
        {
            var samples = SamplesFor(tone.DurationMs);
            var fade = Math.Min(samples / 2, (int)(SampleRate * FadeMs / 1000));
            var phase = 0.0;
            for (var i = 0; i < samples; i++)
            {
                var progress = samples <= 1 ? 0 : (double)i / (samples - 1);
                var frequency = tone.EndFrequency is { } end ? tone.Frequency + ((end - tone.Frequency) * progress) : tone.Frequency;
                phase += frequency / SampleRate;
                phase -= Math.Floor(phase);
                var value = frequency <= 0 ? 0 : Oscillate(tone.Waveform, phase);
                var envelope = Math.Min(1.0, Math.Min((i + 1.0) / Math.Max(1, fade), (samples - i) / (double)Math.Max(1, fade)));
                var sample = (short)Math.Round(value * envelope * tone.Gain * volume * 0.6 * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset, 2), sample);
                offset += 2;
            }
        }

        return wav;
    }

    private static int SamplesFor(int durationMs) => Math.Max(0, SampleRate * durationMs / 1000);

    private static double Oscillate(Waveform waveform, double phase) => waveform switch
    {
        Waveform.Triangle => 1 - (4 * Math.Abs(phase - 0.5)),
        // Softened square (fewer harsh harmonics than a pure square).
        Waveform.Square => Math.Tanh(4 * Math.Sin(2 * Math.PI * phase)) * 0.7,
        _ => Math.Sin(2 * Math.PI * phase),
    };

    private static void WriteHeader(byte[] wav, int totalSamples)
    {
        var span = wav.AsSpan();
        var dataBytes = totalSamples * 2;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
    }
}
