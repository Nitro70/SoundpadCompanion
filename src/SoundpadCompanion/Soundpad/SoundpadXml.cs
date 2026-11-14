using System.Globalization;
using System.Xml.Linq;

namespace SoundpadCompanion.Soundpad;

/// <summary>Parses the XML that GetSoundlist() and GetCategories(...) return.</summary>
public static class SoundpadXml
{
    // <Soundlist><Sound index="1" url="C:\..\a.m4a" artist="" title="a" duration="3:17"
    //   addedOn="2024-01-05" lastPlayedOn="2026-03-02" playCount="42" tag="a"/>...</Soundlist>
    public static IReadOnlyList<Sound> ParseSoundlist(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root is null)
            return [];
        var sounds = new List<Sound>();
        foreach (var element in root.Elements("Sound"))
        {
            if (ParseSound(element) is { } sound)
                sounds.Add(sound);
        }
        return sounds;
    }

    public static Sound? ParseSound(XElement e)
    {
        if (ParseInt(Attr(e, "index")) is not { } index)
            return null;
        return new Sound(
            index,
            Attr(e, "url"),
            Attr(e, "title"),
            Attr(e, "artist"),
            Attr(e, "tag"),
            ParseDuration(Attr(e, "duration")),
            ParseInt(Attr(e, "playCount")) ?? 0,
            ParseDate(Attr(e, "lastPlayedOn")),
            ParseDate(Attr(e, "addedOn")));
    }

    // <Categories><Category index="1" type="1" name="All sounds" hidden="true"/>
    //   <Category index="2" name="My Sounds"><Sound index="4" .../>...</Category>...</Categories>
    public static IReadOnlyList<Category> ParseCategories(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var categories = new List<Category>();
        if (root is not null)
        {
            foreach (var element in root.Elements("Category"))
                Walk(element, parentPath: null, categories);
        }
        return categories;
    }

    private static void Walk(XElement e, string? parentPath, List<Category> into)
    {
        string name = Attr(e, "name");
        string path = parentPath is null ? name : parentPath + " / " + name;
        if (ParseInt(Attr(e, "index")) is { } index)
        {
            var sounds = new List<int>();
            foreach (var sound in e.Elements("Sound"))
            {
                if (ParseInt(Attr(sound, "index")) is { } soundIndex)
                    sounds.Add(soundIndex);
            }
            into.Add(new Category(
                index,
                name,
                path,
                IsSystem: e.Attribute("type") is not null,
                Hidden: string.Equals(Attr(e, "hidden"), "true", StringComparison.OrdinalIgnoreCase),
                sounds));
        }
        foreach (var child in e.Elements("Category"))
            Walk(child, path, into);
    }

    /// <summary>"3:17" or "1:02:03".</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split(':');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (ParseInt(parts[i]) is not { } n || n < 0)
                return null;
            numbers[i] = n;
        }
        return numbers.Length switch
        {
            2 => new TimeSpan(0, numbers[0], numbers[1]),
            3 => new TimeSpan(numbers[0], numbers[1], numbers[2]),
            _ => null,
        };
    }

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    private static int? ParseInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
