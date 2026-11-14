using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

public class SoundpadApiTests
{
    [Fact]
    public async Task Sends_the_documented_command_shapes()
    {
        var fake = new FakeSoundpad();
        fake.Categories.Add(("Memes", []));
        var api = new SoundpadApi(fake);

        await api.PlayAsync(5, PlayTarget.SoundpadDefault);
        await api.PlayAsync(5, PlayTarget.SpeakersOnly);
        await api.PlayAsync(5, PlayTarget.MicOnly);
        await api.PlayAsync(5, PlayTarget.Both);
        await api.AddSoundAsync(@"C:\a b\c.mp3");
        await api.AddSoundToCategoryAsync(@"C:\d.mp3", 2, 1);
        await api.AddCategoryAsync("New one");
        await api.GetCategoriesAsync(withSounds: true);

        Assert.Equal(
        [
            "DoPlaySound(5)",
            "DoPlaySound(5, true, false)",
            "DoPlaySound(5, false, true)",
            "DoPlaySound(5, true, true)",
            "DoAddSound(\"C:\\a b\\c.mp3\")",
            "DoAddSound(\"C:\\d.mp3\", 2, 1)",
            "DoAddCategory(\"New one\", -1)",
            "GetCategories(true, false)",
        ], fake.Commands);
    }

    [Fact]
    public async Task Error_codes_become_command_exceptions()
    {
        var fake = new FakeSoundpad { Override = _ => "R-404" };
        var api = new SoundpadApi(fake);

        var e = await Assert.ThrowsAsync<SoundpadCommandException>(() => api.PlayAsync(99, PlayTarget.SoundpadDefault));
        Assert.Equal("R-404", e.Reply);
        await Assert.ThrowsAsync<SoundpadCommandException>(() => api.GetSoundsAsync());
    }

    [Fact]
    public async Task IsAlive_is_false_when_offline_instead_of_throwing()
    {
        var api = new SoundpadApi(new FakeSoundpad { Online = false });
        Assert.False(await api.IsAliveAsync());
        await Assert.ThrowsAsync<SoundpadOfflineException>(() => api.GetSoundsAsync());
    }

    [Fact]
    public void Refuses_to_send_a_double_quote() =>
        Assert.Throws<ArgumentException>(() => SoundpadApi.Quote("bad\"name"));
}
