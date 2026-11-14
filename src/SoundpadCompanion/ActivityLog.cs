using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;

namespace SoundpadCompanion;

public interface IActivityLog
{
    void Add(string message);
}

/// <summary>What the app did (imports, errors), newest first, shown in Settings and kept in activity.log.</summary>
public sealed class ActivityLog : IActivityLog
{
    private const int MaxEntries = 300;
    private const long MaxFileBytes = 1024 * 1024;

    private readonly Dispatcher _dispatcher;
    private readonly string? _file;
    private readonly object _fileLock = new();

    public ObservableCollection<string> Entries { get; } = [];

    public ActivityLog(Dispatcher dispatcher, string? file)
    {
        _dispatcher = dispatcher;
        _file = file;
        TrimFile();
    }

    public void Add(string message)
    {
        var now = DateTime.Now;
        WriteToFile($"{now:yyyy-MM-dd HH:mm:ss}  {message}");
        string line = $"{now:HH:mm:ss}  {message}";
        _dispatcher.BeginInvoke(() =>
        {
            Entries.Insert(0, line);
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(Entries.Count - 1);
        });
    }

    private void WriteToFile(string line)
    {
        if (_file is null)
            return;
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Keep the newest half once the file passes 1 MB.
    private void TrimFile()
    {
        if (_file is null)
            return;
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length <= MaxFileBytes)
                return;
            var lines = File.ReadAllLines(_file);
            File.WriteAllLines(_file, lines.Skip(lines.Length / 2));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
