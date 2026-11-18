using SoundpadCompanion.Search;
using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

/// <summary>
/// Read-only checks against a real, running Soundpad. Skipped unless SPC_LIVE=1.
/// Sends only Get/Is commands: nothing is played, added or changed.
/// </summary>
public class LiveSoundpadTests
{
    private static bool Live => Environment.GetEnvironmentVariable("SPC_LIVE") == "1";

    [Fact]
    public void Finds_the_volume_adjustment_in_the_running_Soundpad_without_changing_it()
    {
        if (!Live)
            return;
        var saved = SoundpadRegistry.ReadAudioSettings();
        Assert.NotNull(saved);
        var boost = new MicBoost(new SoundpadApi(new SoundpadPipe()), new SoundpadProcessMemory(),
            SoundpadRegistry.ReadAudioSettings, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.4));

        string text = boost.Describe();

        Assert.Contains($"now {saved!.AdjustDb.ToString("+0.#;-0.#;0", System.Globalization.CultureInfo.InvariantCulture)} dB", text);
    }

    [Fact]
    public async Task Reads_the_real_sound_list_and_categories()
    {
        if (!Live)
            return;
        var api = new SoundpadApi(new SoundpadPipe());

        Assert.True(await api.IsAliveAsync());
        Assert.False(string.IsNullOrWhiteSpace(await api.GetVersionAsync()));

        int count = await api.GetSoundCountAsync();
        var sounds = await api.GetSoundsAsync();
        Assert.Equal(count, sounds.Count);
        Assert.Equal(Enumerable.Range(1, count), sounds.Select(s => s.Index));

        var categories = await api.GetCategoriesAsync(withSounds: true);
        Assert.Contains(categories, c => c.IsSystem);
        Assert.All(categories.SelectMany(c => c.SoundIndexes), i => Assert.InRange(i, 1, count));

        var search = new SoundSearch(sounds, categories);
        var first = sounds[0];
        string word = first.DisplayTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains(search.Search(word), e => e.Sound.Index == first.Index);
    }

    [Fact]
    public async Task Many_requests_in_a_row_all_get_answers()
    {
        if (!Live)
            return;
        var api = new SoundpadApi(new SoundpadPipe());
        for (int i = 0; i < 25; i++)
            Assert.True(await api.IsAliveAsync(), $"request {i} got no answer");
    }
}
