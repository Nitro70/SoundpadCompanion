using System.Globalization;

namespace SoundpadCompanion.Soundpad;

/// <summary>
/// The Soundpad remote-control commands this app uses. Command shapes follow Leppsoft's reference
/// implementation (RC 1.1.2); replies are R-200 for OK and other R-xxx codes for errors.
/// </summary>
public sealed class SoundpadApi(ISoundpadTransport transport)
{
    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            return IsOk(await transport.RequestAsync("IsAlive()", ct).ConfigureAwait(false));
        }
        catch (SoundpadOfflineException)
        {
            return false;
        }
    }

    public Task<string> GetVersionAsync(CancellationToken ct = default) => GetAsync("GetVersion()", ct);

    public async Task<int> GetSoundCountAsync(CancellationToken ct = default)
    {
        string reply = await GetAsync("GetSoundFileCount()", ct).ConfigureAwait(false);
        return int.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new SoundpadCommandException("GetSoundFileCount()", reply);
    }

    public async Task<IReadOnlyList<Sound>> GetSoundsAsync(CancellationToken ct = default) =>
        SoundpadXml.ParseSoundlist(await GetAsync("GetSoundlist()", ct).ConfigureAwait(false));

    /// <param name="withSounds">Include each category's sounds (needed for counts and membership).</param>
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(bool withSounds, CancellationToken ct = default) =>
        SoundpadXml.ParseCategories(await GetAsync($"GetCategories({Bool(withSounds)}, false)", ct).ConfigureAwait(false));

    public Task PlayAsync(int index, PlayTarget target, CancellationToken ct = default) => target switch
    {
        PlayTarget.SoundpadDefault => RunAsync($"DoPlaySound({index})", ct),
        PlayTarget.SpeakersOnly => RunAsync($"DoPlaySound({index}, true, false)", ct),
        PlayTarget.MicOnly => RunAsync($"DoPlaySound({index}, false, true)", ct),
        PlayTarget.Both => RunAsync($"DoPlaySound({index}, true, true)", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public Task StopAsync(CancellationToken ct = default) => RunAsync("DoStopSound()", ct);

    /// <summary>Adds a file to the end of "All sounds" only.</summary>
    public Task AddSoundAsync(string path, CancellationToken ct = default) =>
        RunAsync($"DoAddSound({Quote(path)})", ct);

    /// <summary>Adds a file to a category at a position there (1-based; count + 1 appends).</summary>
    public Task AddSoundToCategoryAsync(string path, int categoryIndex, int position, CancellationToken ct = default) =>
        RunAsync($"DoAddSound({Quote(path)}, {categoryIndex}, {position})", ct);

    /// <summary>Adds a category at the bottom of the category list.</summary>
    public Task AddCategoryAsync(string name, CancellationToken ct = default) =>
        RunAsync($"DoAddCategory({Quote(name)}, -1)", ct);

    public static bool IsOk(string reply) => reply.StartsWith("R-200", StringComparison.Ordinal);

    private async Task<string> GetAsync(string command, CancellationToken ct)
    {
        string reply = await transport.RequestAsync(command, ct).ConfigureAwait(false);
        if (reply.Length == 0 || reply.StartsWith("R-", StringComparison.Ordinal))
            throw new SoundpadCommandException(command, reply);
        return reply;
    }

    private async Task RunAsync(string command, CancellationToken ct)
    {
        string reply = await transport.RequestAsync(command, ct).ConfigureAwait(false);
        if (!IsOk(reply))
            throw new SoundpadCommandException(command, reply);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Soundpad takes string arguments in double quotes with no escaping, so a quote cannot be sent.
    /// Windows file names can't contain one; category names are checked before they get here.</summary>
    internal static string Quote(string value)
    {
        if (value.Contains('"'))
            throw new ArgumentException("Soundpad can't take a double quote in a name or path.", nameof(value));
        return "\"" + value + "\"";
    }
}
