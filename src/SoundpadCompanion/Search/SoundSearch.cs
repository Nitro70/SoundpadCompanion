using System.Globalization;
using System.Text;
using SoundpadCompanion.Soundpad;

namespace SoundpadCompanion.Search;

/// <summary>A sound plus the (non-system) categories it sits in, with lowercased search keys.</summary>
public sealed class SoundEntry
{
    public Sound Sound { get; }
    public IReadOnlyList<string> Categories { get; }

    internal string TitleKey { get; }
    internal string ArtistKey { get; }
    internal string TagKey { get; }
    internal string FileKey { get; }
    internal string[] CategoryKeys { get; }

    public SoundEntry(Sound sound, IReadOnlyList<string> categories)
    {
        Sound = sound;
        Categories = categories;
        TitleKey = SoundSearch.Normalize(sound.DisplayTitle);
        ArtistKey = SoundSearch.Normalize(sound.Artist);
        TagKey = SoundSearch.Normalize(sound.Tag);
        FileKey = SoundSearch.Normalize(sound.FileName);
        CategoryKeys = categories.Select(SoundSearch.Normalize).ToArray();
    }
}

/// <summary>
/// Type-ahead search over the sound list. Every word typed has to match somewhere (title, file
/// name, artist, tag or category). Title hits beat everything else, the start of a word beats the
/// middle of one, and sounds played more often win ties.
/// </summary>
public sealed class SoundSearch
{
    public static readonly SoundSearch Empty = new([], []);

    private readonly List<SoundEntry> _entries;

    public SoundSearch(IEnumerable<Sound> sounds, IEnumerable<Category> categories)
    {
        var membership = new Dictionary<int, List<string>>();
        foreach (var category in categories)
        {
            if (category.IsSystem)
                continue;
            foreach (int index in category.SoundIndexes)
            {
                if (!membership.TryGetValue(index, out var names))
                    membership[index] = names = [];
                if (!names.Contains(category.Name))
                    names.Add(category.Name);
            }
        }
        _entries = sounds
            .Select(s => new SoundEntry(s, membership.TryGetValue(s.Index, out var names) ? names : []))
            .ToList();
    }

    public int Count => _entries.Count;

    public SoundEntry? FindByUrl(string url) =>
        _entries.FirstOrDefault(e => string.Equals(e.Sound.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>An empty query lists the most recently played sounds.</summary>
    public IReadOnlyList<SoundEntry> Search(string? query, int limit = 60)
    {
        string[] tokens = Tokenize(query);
        if (tokens.Length == 0)
        {
            return _entries
                .OrderByDescending(e => e.Sound.LastPlayedOn ?? DateOnly.MinValue)
                .ThenByDescending(e => e.Sound.PlayCount)
                .ThenBy(e => e.Sound.Index)
                .Take(limit)
                .ToList();
        }

        var hits = new List<(SoundEntry Entry, double Score, bool Loose)>();
        foreach (var entry in _entries)
        {
            double total = 0;
            bool everyTokenMatched = true;
            bool loose = false;
            foreach (string token in tokens)
            {
                double score = ScoreToken(entry, token);
                if (score <= 0)
                {
                    everyTokenMatched = false;
                    break;
                }
                loose |= score <= LooseScore;
                total += score;
            }
            if (everyTokenMatched)
                hits.Add((entry, total + PopularityBonus(entry.Sound.PlayCount), loose));
        }

        // Skipped-letter matches are a fallback for typos: once anything matches properly, they are
        // noise ("horn" would otherwise also find "shower noises", h-o-r-n in order).
        if (hits.Any(h => !h.Loose))
            hits.RemoveAll(h => h.Loose);

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Sound.Index)
            .Take(limit)
            .Select(h => h.Entry)
            .ToList();
    }

    internal static double ScoreToken(SoundEntry e, string token)
    {
        double best = FieldScore(e.TitleKey, token, start: 100, wordStart: 80, inside: 60);
        best = Math.Max(best, FieldScore(e.FileKey, token, 45, 40, 30));
        best = Math.Max(best, FieldScore(e.ArtistKey, token, 45, 40, 30));
        best = Math.Max(best, FieldScore(e.TagKey, token, 40, 35, 25));
        foreach (string category in e.CategoryKeys)
            best = Math.Max(best, FieldScore(category, token, 30, 28, 20));

        // Loose fallback for skipped letters ("crzy" finds "crazy"), only for 3+ letters
        // so short tokens don't match half the list.
        if (best == 0 && token.Length >= 3 && IsSubsequence(token, e.TitleKey))
            best = LooseScore;
        return best;
    }

    private const double LooseScore = 8;

    private static double FieldScore(string field, string token, double start, double wordStart, double inside)
    {
        if (field.Length == 0)
            return 0;
        int at = field.IndexOf(token, StringComparison.Ordinal);
        if (at < 0)
            return 0;
        if (at == 0)
            return start;
        for (; at >= 0; at = field.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (!char.IsLetterOrDigit(field[at - 1]))
                return wordStart;
        }
        return inside;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        int i = 0;
        foreach (char c in haystack)
        {
            if (c == needle[i] && ++i == needle.Length)
                return true;
        }
        return false;
    }

    // At most +10, so it reorders similar matches but never lifts a weaker kind of match over a stronger one.
    private static double PopularityBonus(int plays) => Math.Min(10, Math.Log2(plays + 1));

    private static string[] Tokenize(string? query) =>
        Normalize(query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lowercase with accents stripped, so "cafe" finds "Café".</summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
