using System.IO;
using System.Text.Json;

namespace SoundpadCompanion.Import;

/// <summary>
/// What the importer has already dealt with, kept in import-state.json:
/// <list type="bullet">
/// <item><b>Seen</b>: files imported, or found already sitting in a folder when it was first watched.
/// The start-up catch-up skips these, so a sound you deleted from Soundpad doesn't keep coming back.</item>
/// <item><b>Baselines</b>: folders (with their subfolder setting) whose existing files were recorded as seen.</item>
/// </list>
/// </summary>
public sealed class ImportState
{
    private readonly string? _file;
    private readonly object _lock = new();
    private readonly object _saveLock = new();   // the importer thread and the UI thread both save
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselines = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Stored
    {
        public List<string> Seen { get; set; } = [];
        public List<string> Baselines { get; set; } = [];
    }

    /// <param name="file">null keeps everything in memory (tests).</param>
    public ImportState(string? file)
    {
        _file = file;
        if (file is null || !File.Exists(file))
            return;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file));
            if (stored is not null)
            {
                _seen.UnionWith(stored.Seen ?? []);
                _baselines.UnionWith(stored.Baselines ?? []);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Losing this only means a start-up catch-up may offer files again; Soundpad duplicates are still skipped.
        }
    }

    public bool IsSeen(string path) { lock (_lock) return _seen.Contains(PathKey.Of(path)); }

    public void MarkSeen(string path) { lock (_lock) _seen.Add(PathKey.Of(path)); }

    public void MarkSeen(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            foreach (string path in paths)
                _seen.Add(PathKey.Of(path));
        }
    }

    public bool HasBaseline(string key) { lock (_lock) return _baselines.Contains(key); }

    public void AddBaseline(string key) { lock (_lock) _baselines.Add(key); }

    public void Save()
    {
        if (_file is null)
            return;
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(new Stored { Seen = [.. _seen], Baselines = [.. _baselines] });
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                string temp = _file + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _file, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>One comparable spelling of a path: full, backslashes, no trailing slash.</summary>
public static class PathKey
{
    public static string Of(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
