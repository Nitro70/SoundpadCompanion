using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

/// <summary>
/// Shift+Enter: Soundpad's "Volume adjustment by" goes to +30 dB in Soundpad's memory for one
/// sound, then back. Soundpad's memory is a byte array here, its pipe the usual fake.
/// </summary>
public class MicBoostTests
{
    private static readonly SoundpadAudioSettings Saved = new(NormalizeVolume: 0, TargetDb: 89f, AverageDb: 98.9f, AdjustDb: -9f);
    private const int Block = 0x400;   // where the test image keeps the settings block

    private sealed class Setup
    {
        public List<string> Events { get; } = [];
        public FakeSoundpad Pipe { get; } = new();
        public FakeSoundpadMemory Memory { get; }
        // Position before the play, then each poll after it; the last one repeats.
        public Queue<string> Positions { get; } = new(["0", "250"]);
        public string PlayReply { get; set; } = "R-200";
        public SoundpadAudioSettings? Settings { get; set; } = Saved;

        public Setup(byte[] image)
        {
            Memory = new FakeSoundpadMemory(image, Events);
            Pipe.Override = command =>
            {
                Events.Add(command);
                if (command.StartsWith("DoPlaySound(", StringComparison.Ordinal))
                    return PlayReply;
                if (command == "GetPlaybackPositionInMs()")
                    return Positions.Count > 1 ? Positions.Dequeue() : Positions.Peek();
                return null;
            };
        }

        public MicBoost Boost(TimeSpan? startLimit = null) =>
            new(new SoundpadApi(Pipe), Memory, () => Settings, startLimit ?? TimeSpan.FromSeconds(2), TimeSpan.Zero);
    }

    private static byte[] Image(params (int Offset, float Adjust)[] blocks)
    {
        var image = new byte[0x1000];
        foreach (var (offset, adjust) in blocks)
        {
            BitConverter.TryWriteBytes(image.AsSpan(offset), Saved.NormalizeVolume);
            BitConverter.TryWriteBytes(image.AsSpan(offset + 4), Saved.TargetDb);
            BitConverter.TryWriteBytes(image.AsSpan(offset + 8), Saved.AverageDb);
            BitConverter.TryWriteBytes(image.AsSpan(offset + 12), adjust);
        }
        return image;
    }

    private static Setup Normal() => new(Image((Block, -9f)));

    [Fact]
    public async Task Boost_writes_30_plays_then_puts_the_saved_value_back()
    {
        var s = Normal();
        await s.Boost().PlayBoostedAsync(5);

        Assert.Equal(["GetPlaybackPositionInMs()", "write 30", "DoPlaySound(5)", "GetPlaybackPositionInMs()", "write -9"], s.Events);
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public async Task Says_the_sound_is_playing_before_the_hold_ends()
    {
        // The overlay closes on this, instead of staying up for the whole hold.
        var s = Normal();
        await s.Boost().PlayBoostedAsync(5, playing: () => s.Events.Add("playing"));

        Assert.Equal(["GetPlaybackPositionInMs()", "write 30", "DoPlaySound(5)", "playing", "GetPlaybackPositionInMs()", "write -9"], s.Events);
    }

    [Fact]
    public async Task Never_says_playing_when_the_play_fails()
    {
        var s = Normal();
        s.PlayReply = "R-404";

        await Assert.ThrowsAsync<SoundpadCommandException>(() => s.Boost().PlayBoostedAsync(5, playing: () => s.Events.Add("playing")));
        Assert.DoesNotContain("playing", s.Events);
    }

    [Fact]
    public async Task Waits_for_the_new_sound_to_start_before_putting_it_back()
    {
        // When stopped, Soundpad still reports where the last sound ended (2073 ms in the real one);
        // the new sound has started once the position drops back below that.
        var s = Normal();
        s.Positions.Clear();
        foreach (string p in new[] { "2073", "2073", "2073", "40" })
            s.Positions.Enqueue(p);

        await s.Boost().PlayBoostedAsync(5);

        Assert.Equal(
            ["GetPlaybackPositionInMs()", "write 30", "DoPlaySound(5)",
             "GetPlaybackPositionInMs()", "GetPlaybackPositionInMs()", "GetPlaybackPositionInMs()", "write -9"],
            s.Events);
    }

    [Fact]
    public async Task Holds_the_boost_for_the_minimum_time_after_the_play()
    {
        var s = Normal();
        var hold = TimeSpan.FromMilliseconds(300);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await new MicBoost(new SoundpadApi(s.Pipe), s.Memory, () => s.Settings, TimeSpan.FromSeconds(2), hold).PlayBoostedAsync(5);

        Assert.True(clock.Elapsed >= hold, $"put back after {clock.Elapsed.TotalMilliseconds} ms");
        Assert.Equal("write -9", s.Events[^1]);
    }

    [Fact]
    public async Task Puts_the_value_back_when_the_play_fails()
    {
        var s = Normal();
        s.PlayReply = "R-404";

        await Assert.ThrowsAsync<SoundpadCommandException>(() => s.Boost().PlayBoostedAsync(5));

        Assert.Equal(["GetPlaybackPositionInMs()", "write 30", "DoPlaySound(5)", "write -9"], s.Events);
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public async Task Puts_the_value_back_when_playback_never_starts()
    {
        var s = Normal();
        s.Positions.Clear();
        s.Positions.Enqueue("0");

        await s.Boost(startLimit: TimeSpan.FromMilliseconds(150)).PlayBoostedAsync(5);

        Assert.Equal("write -9", s.Events[^1]);
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public async Task Refuses_in_dynamic_mode()
    {
        var s = Normal();
        s.Settings = Saved with { NormalizeVolume = 1 };

        var e = await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));

        Assert.Contains("Fixed volume adjustment", e.Message);
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Refuses_when_Soundpad_is_not_running()
    {
        var s = Normal();
        s.Memory.Running = false;

        await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Refuses_when_Soundpads_settings_are_not_in_the_registry()
    {
        var s = Normal();
        s.Settings = null;

        await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Refuses_when_the_block_is_not_found()
    {
        var s = new Setup(Image());

        var e = await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));

        Assert.Contains("version", e.Message);
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Refuses_when_the_block_is_ambiguous()
    {
        var s = new Setup(Image((Block, -9f), (Block + 0x200, -9f)));

        await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task Boost_still_works_when_an_earlier_run_left_it_at_30()
    {
        var s = new Setup(Image((Block, 30f)));

        await s.Boost().PlayBoostedAsync(5);

        Assert.Equal("write -9", s.Events[^1]);
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public async Task Says_so_when_the_value_does_not_go_back()
    {
        var s = Normal();
        s.Memory.IgnoreWritesOf = -9f;

        var e = await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));
        Assert.Contains("+30", e.Message);
    }

    [Fact]
    public void Recover_puts_the_saved_value_back()
    {
        var s = new Setup(Image((Block, 30f)));

        string? message = s.Boost().RecoverIfStuck();

        Assert.NotNull(message);
        Assert.Contains("-9", message);
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public void Recover_does_nothing_when_the_value_is_the_saved_one()
    {
        var s = Normal();

        Assert.Null(s.Boost().RecoverIfStuck());
        Assert.Empty(s.Events);
    }

    [Fact]
    public void Describe_finds_the_block_without_writing()
    {
        var s = Normal();

        string text = s.Boost().Describe();

        Assert.Contains("-9", text);
        Assert.Empty(s.Events);
    }

    [Fact]
    public void FindAdjustment_allows_the_average_to_have_moved_since_it_was_saved()
    {
        var image = Image((Block, -9f));
        BitConverter.TryWriteBytes(image.AsSpan(Block + 8), 97.25f);   // recomputed in memory, registry not rewritten yet

        Assert.Equal([Block + 12], MicBoost.FindAdjustment(image, Saved, -9f));
    }

    [Fact]
    public void FindAdjustment_skips_unaligned_matches()
    {
        var image = Image((Block, -9f));
        var shifted = Image();
        Image((0, -9f)).AsSpan(0, 16).CopyTo(shifted.AsSpan(0x102));   // same bytes, not on a 4-byte boundary
        Image((Block, -9f)).AsSpan(Block, 16).CopyTo(shifted.AsSpan(Block));

        Assert.Equal([Block + 12], MicBoost.FindAdjustment(image, Saved, -9f));
        Assert.Equal([Block + 12], MicBoost.FindAdjustment(shifted, Saved, -9f));
    }

    // ---- final review fixes ----

    [Fact]
    public async Task Waits_through_position_zero_until_the_sound_is_really_playing()
    {
        // Accepted but not producing audio yet (a slow file): the position can sit at 0 first.
        var s = Normal();
        s.Positions.Clear();
        foreach (string p in new[] { "2073", "0", "0", "35" })
            s.Positions.Enqueue(p);

        await s.Boost().PlayBoostedAsync(5);

        Assert.Equal(3, s.Events.Count(e => e == "GetPlaybackPositionInMs()") - 1);
        Assert.Equal("write -9", s.Events[^1]);
    }

    [Fact]
    public async Task Puts_the_value_back_on_time_when_Soundpad_stops_answering()
    {
        var s = Normal();
        var hanging = new HangAfterPlay(s.Pipe);
        var boost = new MicBoost(new SoundpadApi(hanging), s.Memory, () => s.Settings, TimeSpan.FromMilliseconds(200), TimeSpan.Zero);

        var run = boost.PlayBoostedAsync(5);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(run, finished);
        await run;
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public async Task Refuses_when_the_saved_value_is_already_30()
    {
        // Soundpad saves its settings by itself; a boost cut off before Soundpad closed can end up saved.
        var s = new Setup(Image((Block, 30f)));
        s.Settings = Saved with { AdjustDb = 30f };

        var e = await Assert.ThrowsAsync<MicBoostException>(() => s.Boost().PlayBoostedAsync(5));

        Assert.Contains("Preferences", e.Message);
        Assert.Empty(s.Events);
    }

    [Fact]
    public void Recover_warns_when_the_saved_value_is_30()
    {
        var s = new Setup(Image((Block, 30f)));
        s.Settings = Saved with { AdjustDb = 30f };

        string? message = s.Boost().RecoverIfStuck();

        Assert.NotNull(message);
        Assert.Contains("Preferences", message);
        Assert.Empty(s.Events);
    }

    [Fact]
    public async Task A_failed_put_back_is_tried_again()
    {
        var s = Normal();
        s.Memory.IgnoreWritesOf = -9f;
        s.Memory.IgnoreTimes = 2;

        await s.Boost().PlayBoostedAsync(5);

        Assert.Equal(3, s.Events.Count(e => e == "write -9"));
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    [Fact]
    public async Task A_put_back_that_keeps_failing_is_reported_and_owed()
    {
        var s = Normal();
        s.Memory.IgnoreWritesOf = -9f;
        var boost = s.Boost();
        var reported = new List<string>();
        boost.PutBackFailed += reported.Add;

        await Assert.ThrowsAsync<MicBoostException>(() => boost.PlayBoostedAsync(5));

        Assert.Single(reported);
        Assert.Contains("+30", reported[0]);
        Assert.True(boost.Owed);
    }

    [Fact]
    public async Task Recover_settles_an_owed_put_back()
    {
        var s = Normal();
        s.Memory.IgnoreWritesOf = -9f;
        var boost = s.Boost();
        await Assert.ThrowsAsync<MicBoostException>(() => boost.PlayBoostedAsync(5));
        Assert.True(boost.Owed);
        s.Memory.IgnoreWritesOf = null;

        Assert.NotNull(boost.RecoverIfStuck());

        Assert.False(boost.Owed);
        Assert.Equal(-9f, s.Memory.FloatAt(Block + 12));
    }

    /// <summary>Answers like the fake until the play, then never answers a position request (until cancelled).</summary>
    private sealed class HangAfterPlay(FakeSoundpad inner) : ISoundpadTransport
    {
        private bool _played;

        public async Task<string> RequestAsync(string command, CancellationToken ct = default)
        {
            if (_played && command == "GetPlaybackPositionInMs()")
                await Task.Delay(Timeout.Infinite, ct);
            if (command.StartsWith("DoPlaySound(", StringComparison.Ordinal))
                _played = true;
            return await inner.RequestAsync(command, ct);
        }
    }
}

/// <summary>Soundpad.exe's image as a byte array. Writes are logged as "write N" into the shared event list.</summary>
internal sealed class FakeSoundpadMemory(byte[] image, List<string> events) : ISoundpadMemory
{
    public const long Base = 0x7FF7_0000_0000;

    public bool Running { get; set; } = true;
    public float? IgnoreWritesOf { get; set; }
    /// <summary>How many writes of <see cref="IgnoreWritesOf"/> to drop; null drops them all.</summary>
    public int? IgnoreTimes { get; set; }

    public float FloatAt(int offset) => BitConverter.ToSingle(_image, offset);

    public ISoundpadProcess? Open() => Running ? new Process(this) : null;

    private sealed class Process(FakeSoundpadMemory owner) : ISoundpadProcess
    {
        public long ImageBase => Base;
        public int ImageSize => owner._image.Length;

        public byte[] Read(long address, int count) =>
            owner._image.AsSpan((int)(address - Base), count).ToArray();

        public void Write(long address, byte[] data)
        {
            float value = BitConverter.ToSingle(data);
            owner._events.Add($"write {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (owner.IgnoreWritesOf == value && owner.IgnoreTimes is not <= 0)
            {
                if (owner.IgnoreTimes is { } left)
                    owner.IgnoreTimes = left - 1;
                return;
            }
            data.CopyTo(owner._image, (int)(address - Base));
        }

        public void Dispose() { }
    }

    private readonly byte[] _image = image;
    private readonly List<string> _events = events;
}
