using System.IO;
using System.Text.Json;

namespace SoundpadCompanion.Config;

public sealed class AppConfig
{
    public string Hotkey { get; set; } = Hotkeys.Hotkey.Default.ToString();

    /// <summary>Hide the overlay (and hand focus back to the game) once a sound starts.</summary>
    public bool CloseAfterPlay { get; set; } = true;

    /// <summary>On start, import files that landed in a watched folder while the app was closed.</summary>
    public bool CatchUpOnStart { get; set; } = true;

    public List<ImportRule> ImportRules { get; set; } = [];
}

/// <summary>"Files dropped in this folder go into this Soundpad category."</summary>
public sealed class ImportRule
{
    public string Folder { get; set; } = "";

    /// <summary>Category name. Empty means "All sounds" only. Created in Soundpad if missing.</summary>
    public string Category { get; set; } = "";

    public bool IncludeSubfolders { get; set; }

    public bool Enabled { get; set; } = true;

    public ImportRule Clone() => (ImportRule)MemberwiseClone();
}

/// <summary>Reads and writes %AppData%\SoundpadCompanion\config.json.</summary>
public sealed class ConfigStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(directory, "config.json");

    /// <returns>The config, and whether this is the first run (no config file yet).</returns>
    public (AppConfig Config, bool FirstRun) Load(IActivityLog? log = null)
    {
        if (!File.Exists(FilePath))
            return (new AppConfig(), true);
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();
            config.ImportRules ??= [];
            return (config, false);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the broken file for inspection rather than silently overwriting it.
            string backup = FilePath + ".bad";
            try { File.Copy(FilePath, backup, overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            log?.Add($"Settings file was unreadable ({e.Message}); started with defaults. The old file is at {backup}");
            return (new AppConfig(), false);
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
