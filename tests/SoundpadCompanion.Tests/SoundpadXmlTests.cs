using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

public class SoundpadXmlTests
{
    // Shape copied from what Soundpad 4.0.35 returns (paths and titles made up).
    private const string Soundlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Soundlist>
          <Sound index="1" url="C:\Sounds\Airhorn.mp3" artist="" title="Airhorn" duration="0:04" addedOn="2024-01-05" lastPlayedOn="2026-03-02" playCount="42" tag="Airhorn"/>
          <Sound index="2" url="C:\Sounds\Long Song.m4a" artist="Some Band" title="Long Song" duration="1:02:03" addedOn="2023-12-14" lastPlayedOn="" playCount="0" tag="Some Band - Long Song"/>
          <Sound index="x" url="C:\Sounds\broken.mp3" title="no index"/>
        </Soundlist>
        """;

    private const string Categories = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Categories>
          <Category index="1" type="1" name="All sounds" hidden="true"/>
          <Category index="2" name="Memes">
            <Sound index="1" url="C:\Sounds\Airhorn.mp3" title="Airhorn"/>
            <Category index="3" name="Loud">
              <Sound index="1" url="C:\Sounds\Airhorn.mp3" title="Airhorn"/>
              <Sound index="2" url="C:\Sounds\Long Song.m4a" title="Long Song"/>
            </Category>
          </Category>
          <Category index="4" name="Songs"/>
        </Categories>
        """;

    [Fact]
    public void Parses_sounds_and_skips_entries_without_an_index()
    {
        var sounds = SoundpadXml.ParseSoundlist(Soundlist);

        Assert.Equal(2, sounds.Count);
        var airhorn = sounds[0];
        Assert.Equal(1, airhorn.Index);
        Assert.Equal(@"C:\Sounds\Airhorn.mp3", airhorn.Url);
        Assert.Equal(TimeSpan.FromSeconds(4), airhorn.Duration);
        Assert.Equal(42, airhorn.PlayCount);
        Assert.Equal(new DateOnly(2026, 3, 2), airhorn.LastPlayedOn);

        var song = sounds[1];
        Assert.Equal("Some Band", song.Artist);
        Assert.Equal(new TimeSpan(1, 2, 3), song.Duration);
        Assert.Null(song.LastPlayedOn);
    }

    [Fact]
    public void Flattens_nested_categories_with_paths_and_marks_system_ones()
    {
        var categories = SoundpadXml.ParseCategories(Categories);

        Assert.Equal([1, 2, 3, 4], categories.Select(c => c.Index));
        Assert.True(categories[0].IsSystem);
        Assert.True(categories[0].Hidden);
        Assert.Equal("Memes", categories[1].Path);
        Assert.Equal([1], categories[1].SoundIndexes);           // its own sounds, not the child's
        Assert.Equal("Memes / Loud", categories[2].Path);
        Assert.Equal(2, categories[2].SoundCount);
        Assert.Equal(0, categories[3].SoundCount);
        Assert.False(categories[3].IsSystem);
    }

    [Theory]
    [InlineData("0:04", 4)]
    [InlineData("3:17", 197)]
    [InlineData("1:00:00", 3600)]
    public void Reads_durations(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SoundpadXml.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-1:00")]
    public void Bad_durations_are_null(string text) => Assert.Null(SoundpadXml.ParseDuration(text));

    [Fact]
    public void Display_title_falls_back_to_the_file_name()
    {
        var sound = new Sound(1, @"C:\a\Bruh Sound.mp3", "", "", "", null, 0, null, null);
        Assert.Equal("Bruh Sound", sound.DisplayTitle);
    }
}
