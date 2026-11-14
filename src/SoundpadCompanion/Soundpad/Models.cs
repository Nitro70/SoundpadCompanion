using System.IO;

namespace SoundpadCompanion.Soundpad;

/// <summary>One entry of Soundpad's "All sounds" list.</summary>
/// <param name="Index">Soundpad's index for the sound (1-based, the first column in Soundpad). Changes when sounds are added or removed.</param>
public sealed record Sound(
    int Index,
    string Url,
    string Title,
    string Artist,
    string Tag,
    TimeSpan? Duration,
    int PlayCount,
    DateOnly? LastPlayedOn,
    DateOnly? AddedOn)
{
    public string FileName => SafeFileName(Url);

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : FileName;

    private static string SafeFileName(string url)
    {
        try { return Path.GetFileNameWithoutExtension(url); }
        catch (ArgumentException) { return url; }
    }
}

/// <summary>A Soundpad category, flattened out of the category tree.</summary>
/// <param name="Index">Soundpad's category index, as used by DoAddSound and DoPlaySoundFromCategory.</param>
/// <param name="Path">"Parent / Child" for nested categories, otherwise the name.</param>
/// <param name="IsSystem">Built-in categories such as "All sounds" (they carry a type attribute).</param>
/// <param name="SoundIndexes">The sounds in this category, in their order there.</param>
public sealed record Category(
    int Index,
    string Name,
    string Path,
    bool IsSystem,
    bool Hidden,
    IReadOnlyList<int> SoundIndexes)
{
    public int SoundCount => SoundIndexes.Count;
}

public enum PlayTarget
{
    /// <summary>Whatever Soundpad itself is set to do (DoPlaySound(index)).</summary>
    SoundpadDefault,
    SpeakersOnly,
    MicOnly,
    Both,
}
