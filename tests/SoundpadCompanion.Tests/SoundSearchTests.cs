using SoundpadCompanion.Search;
using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

public class SoundSearchTests
{
    private static Sound S(int index, string title, string artist = "", int plays = 0, string? lastPlayed = null, string? file = null) =>
        new(index, file ?? $@"C:\s\{title}.mp3", title, artist, "", null, plays,
            lastPlayed is null ? null : DateOnly.Parse(lastPlayed), null);

    private static readonly Sound[] Sounds =
    [
        S(1, "Drum Roll", "Studio Kit", plays: 90, lastPlayed: "2025-08-01"),
        S(2, "Vine Boom", plays: 3, lastPlayed: "2026-11-01"),
        S(3, "Boom Headshot", plays: 50, lastPlayed: "2026-01-01"),
        S(4, "Airhorn", plays: 0),
        S(5, "Crowd Goes Wild", "Stadium Pack", plays: 40, lastPlayed: "2025-10-01"),
        S(6, "Café Music", plays: 1),
        S(7, "", file: @"C:\s\bruh sound effect.wav"),
    ];

    private static readonly Category[] Categories =
    [
        new(1, "All sounds", "All sounds", IsSystem: true, Hidden: true, [1, 2, 3, 4, 5, 6, 7]),
        new(2, "Memes", "Memes", false, false, [2, 3, 4, 7]),
        new(3, "Songs", "Songs", false, false, [1, 5]),
    ];

    private static readonly SoundSearch Search = new(Sounds, Categories);

    private static int[] Find(string query) => Search.Search(query).Select(e => e.Sound.Index).ToArray();

    [Fact]
    public void Empty_query_lists_most_recently_played_first() =>
        Assert.Equal([2, 3, 5, 1], Find("").Take(4));

    [Fact]
    public void Title_start_beats_word_start_beats_inside()
    {
        // "boom": Vine Boom (word start) vs Boom Headshot (title start)
        Assert.Equal([3, 2], Find("boom"));
    }

    [Fact]
    public void Every_word_must_match()
    {
        Assert.Equal([1], Find("drum studio"));
        Assert.Empty(Find("drum headshot"));
    }

    [Fact]
    public void Matches_categories_artists_and_file_names()
    {
        Assert.Contains(5, Find("songs"));          // category
        Assert.Equal([5], Find("stadium"));         // artist
        Assert.Equal([7], Find("bruh"));            // file name, sound has no title
    }

    [Fact]
    public void Accents_and_case_do_not_matter()
    {
        Assert.Equal([6], Find("CAFE"));
        Assert.Equal([6], Find("café"));
    }

    [Fact]
    public void Skipped_letters_still_find_a_title_but_short_tokens_do_not_go_loose()
    {
        Assert.Equal([5], Find("crwd"));   // letters in order from "crowd goes wild"
        Assert.Empty(Find("zq"));
    }

    [Fact]
    public void Loose_matches_are_dropped_once_something_matches_properly()
    {
        // "horn" is also in "shower noises" letter by letter (h-o-r-n), which is just noise next to "air horn".
        var search = new SoundSearch([S(1, "air horn"), S(2, "shower noises")], []);
        Assert.Equal([1], search.Search("horn").Select(e => e.Sound.Index));
        Assert.Equal([2], search.Search("shwrns").Select(e => e.Sound.Index));   // nothing exact: loose is kept
    }

    [Fact]
    public void Membership_ignores_system_categories()
    {
        var entry = Search.Search("airhorn").Single();
        Assert.Equal(["Memes"], entry.Categories);
    }

    [Fact]
    public void Finds_by_url_ignoring_case()
    {
        Assert.Equal(4, Search.FindByUrl(@"c:\S\AIRHORN.mp3")?.Sound.Index);
        Assert.Null(Search.FindByUrl(@"C:\nope.mp3"));
    }
}
