using System.IO;
using SoundpadCompanion.Config;
using SoundpadCompanion.Import;
using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

public sealed class FolderImporterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("spc-test-").FullName;
    private readonly FakeSoundpad _soundpad = new();
    private readonly ListLog _log = new();
    private readonly ImportState _state = new(file: null);
    private readonly FolderImporter _importer;

    public FolderImporterTests()
    {
        _soundpad.Categories.Add(("My Sounds", []));
        _soundpad.Categories.Add(("Memes", []));
        _soundpad.Sounds.Add(new FakeSoundpad.FakeSound(@"C:\old\one.mp3"));
        _soundpad.Categories[1].Sounds.Add(1);
        _importer = new FolderImporter(new SoundpadApi(_soundpad), _log, _state, waitUntilReady: (_, _) => Task.FromResult(true))
        {
            OfflineRetry = TimeSpan.FromMilliseconds(20),
            BatchSettle = TimeSpan.Zero,
        };
    }

    public void Dispose()
    {
        _importer.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string File(string name)
    {
        string path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static ImportRule Rule(string category = "Memes") => new() { Folder = "unused", Category = category };

    [Fact]
    public async Task Appends_new_files_to_the_end_of_the_category()
    {
        string a = File("a.mp3"), b = File("b.wav");

        await _importer.ProcessBatchAsync([new(a, Rule()), new(b, Rule())], CancellationToken.None);

        // Memes (index 3) held 1 sound: positions 2 then 3.
        Assert.Contains($"DoAddSound(\"{a}\", 3, 2)", _soundpad.Commands);
        Assert.Contains($"DoAddSound(\"{b}\", 3, 3)", _soundpad.Commands);
        Assert.Equal([1, 2, 3], _soundpad.Categories[1].Sounds);
        Assert.True(_state.IsSeen(a));
    }

    [Fact]
    public async Task Skips_files_already_in_Soundpad_and_duplicates_in_one_batch()
    {
        string a = File("a.mp3");
        _soundpad.Sounds.Add(new FakeSoundpad.FakeSound(a.ToUpperInvariant()));   // same file, other case

        await _importer.ProcessBatchAsync([new(a, Rule()), new(a, Rule())], CancellationToken.None);

        Assert.DoesNotContain(_soundpad.Commands, c => c.StartsWith("DoAddSound"));
        Assert.Contains(_log.Lines, l => l.StartsWith("Already in Soundpad"));
    }

    [Fact]
    public async Task Creates_a_missing_category_then_adds_into_it()
    {
        string a = File("a.mp3");

        await _importer.ProcessBatchAsync([new(a, Rule("Brand New"))], CancellationToken.None);

        Assert.Contains("DoAddCategory(\"Brand New\", -1)", _soundpad.Commands);
        Assert.Contains($"DoAddSound(\"{a}\", 4, 1)", _soundpad.Commands);
    }

    [Fact]
    public async Task Blank_category_adds_to_all_sounds_only()
    {
        string a = File("a.mp3");

        await _importer.ProcessBatchAsync([new(a, Rule(""))], CancellationToken.None);

        Assert.Contains($"DoAddSound(\"{a}\")", _soundpad.Commands);
        Assert.DoesNotContain(_soundpad.Commands, c => c.StartsWith("GetCategories"));
    }

    [Fact]
    public async Task Ignores_files_that_are_not_audio()
    {
        string text = File("notes.txt"), partial = File("song.mp3.crdownload");

        await _importer.ProcessBatchAsync([new(text, Rule()), new(partial, Rule())], CancellationToken.None);

        Assert.Empty(_soundpad.Commands);
    }

    [Fact]
    public async Task Waits_for_Soundpad_to_start()
    {
        string a = File("a.mp3");
        _soundpad.Online = false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _soundpad.Online = true;
        });

        await _importer.ProcessBatchAsync([new(a, Rule())], CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.Contains("will be imported once it is"));
        Assert.Contains($"DoAddSound(\"{a}\", 3, 2)", _soundpad.Commands);
    }

    [Fact]
    public async Task A_refused_file_is_logged_and_not_retried_at_next_start()
    {
        string a = File("a.mp3");
        _soundpad.Override = c => c.StartsWith("DoAddSound") ? "R-500" : null;

        await _importer.ProcessBatchAsync([new(a, Rule())], CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.Contains("refused") && l.Contains("R-500"));
        Assert.True(_state.IsSeen(a));
    }

    [Fact]
    public void Existing_files_are_left_alone_when_a_folder_is_first_watched()
    {
        string before = File("before.mp3");
        var rule = new ImportRule { Folder = _dir, Category = "Memes" };

        _importer.Apply([rule], catchUp: true);

        Assert.True(_state.IsSeen(before));
        Assert.Contains(_log.Lines, l => l.Contains("Left the 1 file(s) already there alone"));
    }

    [Fact]
    public async Task End_to_end_a_dropped_file_lands_in_Soundpad()
    {
        var rule = new ImportRule { Folder = _dir, Category = "Memes" };
        _importer.Apply([rule], catchUp: true);

        string dropped = File("dropped.mp3");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_soundpad.Commands.Contains($"DoAddSound(\"{dropped}\", 3, 2)") && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Contains($"DoAddSound(\"{dropped}\", 3, 2)", _soundpad.Commands);
    }

    [Fact]
    public void Prefers_the_full_path_then_the_name_and_never_a_system_category()
    {
        Category[] categories =
        [
            new(1, "All sounds", "All sounds", true, true, []),
            new(2, "Loud", "Memes / Loud", false, false, []),
            new(3, "Loud", "Loud", false, false, []),
        ];
        Assert.Equal(3, FolderImporter.FindCategory(categories, "loud")?.Index);
        Assert.Equal(2, FolderImporter.FindCategory(categories, "Memes / Loud")?.Index);
        Assert.Null(FolderImporter.FindCategory(categories, "All sounds"));
    }
}
