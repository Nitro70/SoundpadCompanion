using System.IO;
using System.Threading.Channels;
using SoundpadCompanion.Config;
using SoundpadCompanion.Soundpad;

namespace SoundpadCompanion.Import;

public sealed record ImportJob(string Path, ImportRule Rule);

/// <summary>
/// Watches the folders in the import rules and adds new audio files to Soundpad.
/// <para>A folder's existing files are left alone the first time it is watched (they are only
/// recorded); "Import existing" adds them on request. Files dropped while the app was closed are
/// picked up at the next start. Anything already in Soundpad is never added twice.</para>
/// </summary>
public sealed class FolderImporter : IDisposable
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".oga", ".opus", ".flac", ".m4a", ".aac", ".wma", ".aif", ".aiff",
        ".ape", ".mpc", ".ac3", ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".avi",
    };

    private readonly SoundpadApi _api;
    private readonly IActivityLog _log;
    private readonly ImportState _state;
    private readonly Func<string, CancellationToken, Task<bool>> _waitUntilReady;
    private readonly Channel<ImportJob> _queue = Channel.CreateUnbounded<ImportJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public TimeSpan OfflineRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Lets a burst of dropped files land in one batch (one sound-list fetch).</summary>
    public TimeSpan BatchSettle { get; init; } = TimeSpan.FromMilliseconds(400);

    public FolderImporter(SoundpadApi api, IActivityLog log, ImportState state,
        Func<string, CancellationToken, Task<bool>>? waitUntilReady = null)
    {
        _api = api;
        _log = log;
        _state = state;
        _waitUntilReady = waitUntilReady ?? WaitUntilFileIsCompleteAsync;
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>(Re)starts watching. Call again whenever the rules change.</summary>
    /// <param name="catchUp">Queue files that arrived while the app was closed (use at start-up).</param>
    public void Apply(IEnumerable<ImportRule> rules, bool catchUp)
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();

            foreach (var original in rules)
            {
                var rule = original.Clone();
                if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Folder))
                    continue;
                if (!Directory.Exists(rule.Folder))
                {
                    _log.Add($"Import folder not found: {rule.Folder}");
                    continue;
                }

                string baseline = BaselineKey(rule);
                if (!_state.HasBaseline(baseline))
                {
                    var existing = EnumerateSupported(rule);
                    _state.MarkSeen(existing);
                    _state.AddBaseline(baseline);
                    _state.Save();
                    _log.Add(existing.Count == 0
                        ? $"Watching {rule.Folder}"
                        : $"Watching {rule.Folder}. Left the {existing.Count} file(s) already there alone; use Import existing to add them.");
                }
                else if (catchUp)
                {
                    int queued = 0;
                    foreach (string file in EnumerateSupported(rule))
                    {
                        if (!_state.IsSeen(file))
                        {
                            Enqueue(file, rule);
                            queued++;
                        }
                    }
                    if (queued > 0)
                        _log.Add($"{queued} new file(s) in {rule.Folder} since last time; importing.");
                }

                _watchers.Add(CreateWatcher(rule));
            }
        }
        _worker ??= Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Queues every supported file in the rule's folder. Ones already in Soundpad are skipped.</summary>
    public int ImportExisting(ImportRule rule)
    {
        if (!Directory.Exists(rule.Folder))
        {
            _log.Add($"Import folder not found: {rule.Folder}");
            return 0;
        }
        var copy = rule.Clone();
        var files = EnumerateSupported(copy);
        foreach (string file in files)
            Enqueue(file, copy);
        _log.Add($"Importing {files.Count} file(s) from {rule.Folder}.");
        _worker ??= Task.Run(() => RunAsync(_stop.Token));
        return files.Count;
    }

    private FileSystemWatcher CreateWatcher(ImportRule rule)
    {
        var watcher = new FileSystemWatcher(rule.Folder)
        {
            IncludeSubdirectories = rule.IncludeSubfolders,
            NotifyFilter = NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => Enqueue(e.FullPath, rule);
        // Browsers download to a temp name and rename it at the end, so a rename to .mp3 is a new file too.
        watcher.Renamed += (_, e) => Enqueue(e.FullPath, rule);
        watcher.Error += (_, e) =>
        {
            _log.Add($"Lost track of changes in {rule.Folder} ({e.GetException().Message}); rescanning.");
            foreach (string file in EnumerateSupported(rule))
            {
                if (!_state.IsSeen(file))
                    Enqueue(file, rule);
            }
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Enqueue(string path, ImportRule rule)
    {
        if (IsSupported(path))
            _queue.Writer.TryWrite(new ImportJob(path, rule));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(BatchSettle, ct).ConfigureAwait(false);
                var batch = new List<ImportJob>();
                while (_queue.Reader.TryRead(out var job))
                    batch.Add(job);
                try
                {
                    await ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _log.Add($"Import failed: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Imports one batch against a single snapshot of Soundpad's list.</summary>
    internal async Task ProcessBatchAsync(IReadOnlyList<ImportJob> batch, CancellationToken ct)
    {
        // The same file can be reported more than once (created, then renamed...).
        var jobs = batch
            .Where(j => IsSupported(j.Path))
            .GroupBy(j => PathKey.Of(j.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        var ready = new List<ImportJob>();
        foreach (var job in jobs)
        {
            if (await _waitUntilReady(job.Path, ct).ConfigureAwait(false))
                ready.Add(job);
            else if (File.Exists(job.Path))
                _log.Add($"Skipped {Path.GetFileName(job.Path)}: it was still being written after 2 minutes.");
        }
        if (ready.Count == 0)
            return;

        bool warned = false;
        while (!await _api.IsAliveAsync(ct).ConfigureAwait(false))
        {
            if (!warned)
            {
                _log.Add($"Soundpad isn't running; {ready.Count} file(s) will be imported once it is.");
                warned = true;
            }
            await Task.Delay(OfflineRetry, ct).ConfigureAwait(false);
        }

        var sounds = await _api.GetSoundsAsync(ct).ConfigureAwait(false);
        var inSoundpad = new HashSet<string>(sounds.Select(s => PathKey.Of(s.Url)), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<Category>? categories = null;
        // Sounds this batch has put in each category since `categories` was fetched.
        var addedSinceFetch = new Dictionary<int, int>();

        for (int i = 0; i < ready.Count; i++)
        {
            var job = ready[i];
            string key = PathKey.Of(job.Path);
            string name = Path.GetFileName(job.Path);
            if (inSoundpad.Contains(key))
            {
                _state.MarkSeen(key);
                _log.Add($"Already in Soundpad: {name}");
                continue;
            }

            try
            {
                string categoryName = job.Rule.Category.Trim();
                if (categoryName.Length == 0)
                {
                    await _api.AddSoundAsync(job.Path, ct).ConfigureAwait(false);
                    _log.Add($"Added {name}");
                }
                else
                {
                    categories ??= await _api.GetCategoriesAsync(withSounds: true, ct).ConfigureAwait(false);
                    var category = FindCategory(categories, categoryName);
                    if (category is null)
                    {
                        await _api.AddCategoryAsync(categoryName, ct).ConfigureAwait(false);
                        _log.Add($"Created category {categoryName}");
                        categories = await _api.GetCategoriesAsync(withSounds: true, ct).ConfigureAwait(false);
                        addedSinceFetch.Clear();
                        category = FindCategory(categories, categoryName)
                            ?? throw new SoundpadException($"Soundpad didn't create the category \"{categoryName}\".");
                    }

                    int already = addedSinceFetch.GetValueOrDefault(category.Index);
                    int position = category.SoundCount + already + 1;   // positions are 1-based: count + 1 appends
                    await _api.AddSoundToCategoryAsync(job.Path, category.Index, position, ct).ConfigureAwait(false);
                    addedSinceFetch[category.Index] = already + 1;
                    _log.Add($"Added {name} to {category.Name}");
                }
                inSoundpad.Add(key);
                _state.MarkSeen(key);
            }
            catch (SoundpadCommandException e)
            {
                // Refused (unsupported file, bad path...). Mark it seen so a restart doesn't retry it forever.
                _state.MarkSeen(key);
                _log.Add($"Soundpad refused {name}: {e.Reply}");
            }
            catch (SoundpadOfflineException)
            {
                _log.Add("Lost Soundpad partway through; the rest will be imported when it's back.");
                for (int j = i; j < ready.Count; j++)
                    _queue.Writer.TryWrite(ready[j]);
                break;
            }
            catch (ArgumentException e)
            {
                _state.MarkSeen(key);
                _log.Add($"Can't import {name}: {e.Message}");
            }
        }
        _state.Save();
    }

    internal static Category? FindCategory(IReadOnlyList<Category> categories, string name)
    {
        var candidates = categories.Where(c => !c.IsSystem).ToList();
        return candidates.FirstOrDefault(c => string.Equals(c.Path, name, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string BaselineKey(ImportRule rule) =>
        PathKey.Of(rule.Folder) + (rule.IncludeSubfolders ? "|subfolders" : "|top");

    private List<string> EnumerateSupported(ImportRule rule)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = rule.IncludeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            return Directory.EnumerateFiles(rule.Folder, "*", options).Where(IsSupported).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Add($"Couldn't read {rule.Folder}: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// True once nothing is writing the file: it opens exclusively and its size holds still for half
    /// a second (one exclusive open is enough for a file untouched for a few seconds). False if it
    /// disappears or is still busy after two minutes.
    /// </summary>
    public static async Task<bool> WaitUntilFileIsCompleteAsync(string path, CancellationToken ct)
    {
        long lastSize = -1;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path))
                return false;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    long size = stream.Length;
                    bool settled = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(3);
                    if (size > 0 && (settled || size == lastSize))
                        return true;
                    lastSize = size;
                }
            }
            catch (IOException)
            {
                lastSize = -1;   // still open in the program writing it
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();
        }
        _queue.Writer.TryComplete();
        _state.Save();
    }
}
