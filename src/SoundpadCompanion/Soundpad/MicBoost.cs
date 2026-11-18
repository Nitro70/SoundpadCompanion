using System.Diagnostics;
using System.Globalization;

namespace SoundpadCompanion.Soundpad;

/// <summary>Soundpad's saved audio settings (Preferences, Audio, Volume normalization), from the registry.</summary>
/// <param name="NormalizeVolume">0 is "Fixed volume adjustment", 1 is "Dynamic volume adjustment".</param>
/// <param name="AdjustDb">"Volume adjustment by" in Fixed mode: what sounds get on the mic, in dB.</param>
public sealed record SoundpadAudioSettings(int NormalizeVolume, float TargetDb, float AverageDb, float AdjustDb)
{
    public bool FixedMode => NormalizeVolume == 0;
}

/// <summary>Reaches the running Soundpad.exe.</summary>
public interface ISoundpadMemory
{
    /// <summary>The running Soundpad, or null when it isn't running. Throws <see cref="MicBoostException"/> when Windows refuses access.</summary>
    ISoundpadProcess? Open();
}

/// <summary>Soundpad.exe's own image in memory.</summary>
public interface ISoundpadProcess : IDisposable
{
    long ImageBase { get; }
    int ImageSize { get; }
    byte[] Read(long address, int count);
    void Write(long address, byte[] data);
}

/// <summary>
/// Plays one sound with Soundpad's "Volume adjustment by" at +30 dB, then puts the saved value back.
/// <para>
/// Soundpad's remote control has no command for that setting. While Soundpad runs it keeps it in a
/// settings block inside Soundpad.exe: the normalization mode (int), then the target, average and
/// adjustment in dB (floats), in the order and with the values it saves to the registry. Soundpad
/// reads the adjustment when a sound starts and applies it only to what goes to the mic, so the
/// sound is boosted for whoever hears the mic and unchanged in the speakers. Measured on Soundpad
/// 4.0.35: put back 0.4 s after the play command, the whole sound still went out boosted.
/// </para>
/// </summary>
/// <param name="startLimit">How long to wait for the sound to start before putting the value back anyway.</param>
/// <param name="hold">Never put the value back sooner than this after the play command.</param>
public sealed class MicBoost(
    SoundpadApi api, ISoundpadMemory memory, Func<SoundpadAudioSettings?> settings, TimeSpan startLimit, TimeSpan hold)
{
    public const float BoostDb = 30f;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(15);
    private const int PutBackAttempts = 3;
    private static readonly TimeSpan PutBackRetry = TimeSpan.FromMilliseconds(50);

    private const string SavedAt30 =
        "Soundpad's saved \"Volume adjustment by\" is +30 dB, so every sound is boosted on the mic. " +
        "If you didn't choose that, set it back in Soundpad's Preferences, Audio.";

    private volatile bool _owed;

    /// <summary>Putting the saved value back failed even after retrying (raised off the UI thread): Soundpad may be stuck at +30 dB.</summary>
    public event Action<string>? PutBackFailed;

    /// <summary>A put-back failed and hasn't been settled since, so Soundpad may still be at +30 dB.</summary>
    public bool Owed => _owed;

    /// <param name="playing">Called (off the UI thread) as soon as Soundpad has accepted the play, before the hold ends.</param>
    public async Task PlayBoostedAsync(int index, Action? playing = null, CancellationToken ct = default)
    {
        var saved = Saved();
        if (!saved.FixedMode)
            throw new MicBoostException("The +30 dB boost needs Soundpad set to \"Fixed volume adjustment\" (Preferences, Audio).");
        if (saved.AdjustDb == BoostDb)
            throw new MicBoostException(SavedAt30);
        using var process = Open();
        long address = Locate(process, saved);
        long before = await api.GetPlaybackPositionAsync(ct).ConfigureAwait(false);

        WriteFloat(process, address, BoostDb);
        try
        {
            long played = Stopwatch.GetTimestamp();
            await api.PlayAsync(index, PlayTarget.SoundpadDefault, ct).ConfigureAwait(false);
            playing?.Invoke();
            await WaitForStartAsync(before, ct).ConfigureAwait(false);
            var left = hold - Stopwatch.GetElapsedTime(played);
            if (left > TimeSpan.Zero)
                await Task.Delay(left, ct).ConfigureAwait(false);
        }
        finally
        {
            PutBack(process, address, saved.AdjustDb);
        }
    }

    /// <summary>
    /// At start-up: if a boost was cut off (the app closed or crashed in the middle of one),
    /// Soundpad is still at +30 dB. Puts the saved value back and says so; null when nothing was wrong.
    /// </summary>
    public string? RecoverIfStuck()
    {
        if (settings() is not { } saved)
            return null;
        if (saved.AdjustDb == BoostDb)
            return SavedAt30;   // Soundpad saved a boost that was cut off (or he chose +30): say so, change nothing
        using var process = memory.Open();
        if (process is null)
        {
            _owed = false;   // nothing in memory to fix while Soundpad isn't running
            return null;
        }
        byte[] image = process.Read(process.ImageBase, process.ImageSize);
        if (FindAdjustment(image, saved, saved.AdjustDb).Count > 0)
        {
            _owed = false;
            return null;
        }
        var stuck = FindAdjustment(image, saved, BoostDb);
        if (stuck.Count != 1)
            return null;
        PutBack(process, process.ImageBase + stuck[0], saved.AdjustDb);
        return $"Soundpad's mic adjustment had been left at +30 dB; put it back to {Db(saved.AdjustDb)}.";
    }

    /// <summary>Read-only check for --selftest: where the setting is and what it is now.</summary>
    public string Describe()
    {
        var saved = Saved();
        using var process = Open();
        long address = Locate(process, saved);
        return $"found at Soundpad.exe+0x{address - process.ImageBase:x}, now {Db(ReadFloat(process, address))}"
            + (saved.FixedMode ? "" : ", but Soundpad is in Dynamic mode so Shift won't boost");
    }

    /// <summary>
    /// Offsets in <paramref name="image"/> of the adjustment of every 4-byte-aligned settings block
    /// whose mode and target match <paramref name="saved"/> and whose adjustment is <paramref name="adjustDb"/>.
    /// The average is only checked to be a plausible dB figure: Soundpad recomputes it, and the registry
    /// copy can lag behind.
    /// </summary>
    public static IReadOnlyList<int> FindAdjustment(ReadOnlySpan<byte> image, SoundpadAudioSettings saved, float adjustDb)
    {
        Span<byte> head = stackalloc byte[8];
        BitConverter.TryWriteBytes(head, saved.NormalizeVolume);
        BitConverter.TryWriteBytes(head[4..], saved.TargetDb);
        var found = new List<int>();
        int start = 0;
        while (start <= image.Length - 16)
        {
            int i = image[start..].IndexOf(head);
            if (i < 0 || start + i > image.Length - 16)
                break;
            int at = start + i;
            float average = BitConverter.ToSingle(image[(at + 8)..]);
            if (at % 4 == 0
                && float.IsFinite(average) && average is > 0f and < 200f
                && BitConverter.ToSingle(image[(at + 12)..]) == adjustDb)
                found.Add(at + 12);
            start = at + 1;
        }
        return found;
    }

    private async Task WaitForStartAsync(long before, CancellationToken ct)
    {
        // Stopped, Soundpad still reports where the last sound ended; a new sound starts again from 0,
        // and can sit at 0 until its audio flows. Started = above 0 and below where it was before.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(startLimit);
        try
        {
            while (true)
            {
                long now = await api.GetPlaybackPositionAsync(limit.Token).ConfigureAwait(false);
                if (now > 0 && (before <= 0 || now < before))
                    return;
                await Task.Delay(Poll, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not started within the limit, or Soundpad stopped answering: put it back anyway.
        }
    }

    private SoundpadAudioSettings Saved() =>
        settings() ?? throw new MicBoostException("Couldn't read Soundpad's audio settings from the registry.");

    private ISoundpadProcess Open() =>
        memory.Open() ?? throw new MicBoostException("Soundpad isn't running.");

    private static long Locate(ISoundpadProcess process, SoundpadAudioSettings saved)
    {
        byte[] image = process.Read(process.ImageBase, process.ImageSize);
        var found = FindAdjustment(image, saved, saved.AdjustDb);
        if (found.Count == 0 && saved.AdjustDb != BoostDb)
            found = FindAdjustment(image, saved, BoostDb);   // an earlier boost was cut off
        return found.Count switch
        {
            1 => process.ImageBase + found[0],
            0 => throw new MicBoostException("This Soundpad version isn't recognised: its volume adjustment wasn't found in memory."),
            _ => throw new MicBoostException("This Soundpad version isn't recognised: its volume adjustment was found in more than one place."),
        };
    }

    /// <summary>Writes the saved value back and checks it, a few times if need be. Failing that, the
    /// boost is owed: raises <see cref="PutBackFailed"/> and throws.</summary>
    private void PutBack(ISoundpadProcess process, long address, float value)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < PutBackAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(PutBackRetry);
            try
            {
                WriteFloat(process, address, value);
                if (ReadFloat(process, address) == value)
                {
                    _owed = false;
                    return;
                }
            }
            catch (MicBoostException e)
            {
                last = e;
            }
        }
        _owed = true;
        string message = $"Couldn't put Soundpad's mic adjustment back to {Db(value)}, so it may still be at +30 dB. " +
            "Restart Soundpad Companion to fix it, before closing Soundpad (Soundpad saves its settings and could keep +30).";
        PutBackFailed?.Invoke(message);
        throw new MicBoostException(message, last);
    }

    private static void WriteFloat(ISoundpadProcess process, long address, float value) =>
        process.Write(address, BitConverter.GetBytes(value));

    private static float ReadFloat(ISoundpadProcess process, long address) =>
        BitConverter.ToSingle(process.Read(address, 4));

    private static string Db(float value) => value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " dB";
}
