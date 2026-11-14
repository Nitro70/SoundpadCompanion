using System.IO;
using SoundpadCompanion.Soundpad;

namespace SoundpadCompanion.Tests;

/// <summary>A pretend Soundpad: a sound list and category tree in memory, answering pipe commands.</summary>
internal sealed class FakeSoundpad : ISoundpadTransport
{
    public sealed record FakeSound(string Url, string Title = "", string Artist = "", string Duration = "0:05");

    private readonly List<string> _commands = [];
    private volatile bool _online = true;

    public List<FakeSound> Sounds { get; } = [];
    public List<(string Name, List<int> Sounds)> Categories { get; } = [];
    public Func<string, string?>? Override { get; set; }

    /// <summary>A copy, safe to read while the importer's thread is still sending.</summary>
    public List<string> Commands { get { lock (_commands) return [.. _commands]; } }

    public bool Online { get => _online; set => _online = value; }

    public Task<string> RequestAsync(string command, CancellationToken ct = default)
    {
        lock (_commands)
        {
            _commands.Add(command);
            if (!_online)
                throw new SoundpadOfflineException("Soundpad isn't running.");
            if (Override?.Invoke(command) is { } custom)
                return Task.FromResult(custom);
            return Task.FromResult(Handle(command));
        }
    }

    private string Handle(string command)
    {
        if (command == "IsAlive()")
            return "R-200";
        if (command == "GetSoundlist()")
            return SoundlistXml();
        if (command.StartsWith("GetCategories(", StringComparison.Ordinal))
            return CategoriesXml();
        if (command.StartsWith("DoAddCategory(\"", StringComparison.Ordinal))
        {
            string name = command.Split('"')[1];
            Categories.Add((name, []));
            return "R-200";
        }
        if (command.StartsWith("DoAddSound(\"", StringComparison.Ordinal))
        {
            var parts = command.Split('"');
            string url = parts[1];
            Sounds.Add(new FakeSound(url, Path.GetFileNameWithoutExtension(url)));
            int index = Sounds.Count;
            string rest = parts[2].Trim(' ', ',', ')');
            if (rest.Length > 0)
            {
                var numbers = rest.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
                int category = numbers[0];          // 1 is the hidden "All sounds"
                int position = numbers[1];
                var target = Categories[category - 2].Sounds;
                if (position < 1 || position > target.Count + 1)
                    return "R-400";
                target.Insert(position - 1, index);
            }
            return "R-200";
        }
        if (command.StartsWith("DoPlaySound(", StringComparison.Ordinal))
            return "R-200";
        return "R-404";
    }

    private string SoundlistXml()
    {
        var lines = Sounds.Select((s, i) =>
            $"  <Sound index=\"{i + 1}\" url=\"{Esc(s.Url)}\" artist=\"{Esc(s.Artist)}\" title=\"{Esc(s.Title)}\" duration=\"{s.Duration}\" addedOn=\"2026-01-01\" lastPlayedOn=\"2026-01-0{(i % 9) + 1}\" playCount=\"{i}\" tag=\"\"/>");
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Soundlist>\n" + string.Join("\n", lines) + "\n</Soundlist>";
    }

    private string CategoriesXml()
    {
        var sb = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Categories>\n");
        sb.Append("  <Category index=\"1\" type=\"1\" name=\"All sounds\" hidden=\"true\"/>\n");
        for (int c = 0; c < Categories.Count; c++)
        {
            sb.Append($"  <Category index=\"{c + 2}\" name=\"{Esc(Categories[c].Name)}\">\n");
            foreach (int index in Categories[c].Sounds)
                sb.Append($"    <Sound index=\"{index}\" url=\"{Esc(Sounds[index - 1].Url)}\" title=\"x\"/>\n");
            sb.Append("  </Category>\n");
        }
        return sb.Append("</Categories>").ToString();
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}

internal sealed class ListLog : IActivityLog
{
    public List<string> Lines { get; } = [];
    public void Add(string message) { lock (Lines) Lines.Add(message); }
}
