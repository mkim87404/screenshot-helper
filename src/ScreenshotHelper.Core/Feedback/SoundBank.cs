namespace ScreenshotHelper.Core.Feedback;

/// <summary>
/// The app's sound palette (spec §7). Families share a timbre so they're learnable: captures and toggles are sine,
/// pause/resume are triangle "clicks", errors are a buzz.
/// </summary>
public sealed class SoundBank
{
    private readonly Dictionary<FeedbackKind, byte[]> _sounds;

    private SoundBank(Dictionary<FeedbackKind, byte[]> sounds)
    {
        _sounds = sounds;
    }

    public static IReadOnlyDictionary<FeedbackKind, IReadOnlyList<Tone>> Definitions { get; } = new Dictionary<FeedbackKind, IReadOnlyList<Tone>>
    {
        [FeedbackKind.SessionStarted] = [new(523, 70), new(659, 70), new(784, 110)],
        [FeedbackKind.MainSaved] = [new(520, 85)],
        [FeedbackKind.SubSaved] = [new(780, 85)],
        [FeedbackKind.TimestampArmed] = [new(880, 60), new(1175, 80)],
        [FeedbackKind.TimestampDisarmed] = [new(1175, 60), new(880, 80)],
        [FeedbackKind.CaptionArmed] = [new(1480, 30, Gain: 0.7), new(0, 25), new(1760, 40, Gain: 0.7)],
        [FeedbackKind.CaptionCancelled] = [new(900, 110, Gain: 0.6, EndFrequency: 600)],
        [FeedbackKind.Paused] = [new(392, 55, Waveform.Triangle), new(0, 20), new(262, 75, Waveform.Triangle)],
        [FeedbackKind.Resumed] = [new(262, 55, Waveform.Triangle), new(0, 20), new(392, 75, Waveform.Triangle)],
        [FeedbackKind.Undo] = [new(900, 160, Gain: 0.8, EndFrequency: 400)],
        [FeedbackKind.Warning] = [new(1000, 55), new(0, 55), new(1000, 55)],
        [FeedbackKind.Error] = [new(180, 260, Waveform.Square)],
        [FeedbackKind.SessionEnded] = [new(784, 70), new(659, 70), new(523, 130)],
    };

    /// <summary>Renders every sound at <paramref name="volumePercent"/> (0–100).</summary>
    public static SoundBank Create(int volumePercent)
    {
        var volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
        return new SoundBank(Definitions.ToDictionary(d => d.Key, d => ToneSynth.Render(d.Value, volume)));
    }

    public byte[] this[FeedbackKind kind] => _sounds[kind];
}
