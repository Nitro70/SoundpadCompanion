using SoundpadCompanion.Search;
using SoundpadCompanion.Ui;

namespace SoundpadCompanion.Overlay;

/// <summary>One line in the overlay's result list.</summary>
public sealed class SoundRow(SoundEntry entry)
{
    public int Index { get; } = entry.Sound.Index;
    public string Url { get; } = entry.Sound.Url;
    public string Title { get; } = entry.Sound.DisplayTitle;

    public string Subtitle { get; } = string.Join("  ·  ",
        new[] { entry.Sound.Artist }.Concat(entry.Categories).Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool HasSubtitle => Subtitle.Length > 0;

    public string Duration { get; } = entry.Sound.Duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : "";
}

public sealed class OverlayViewModel : ObservableObject
{
    private SoundSearch _library = SoundSearch.Empty;
    private bool _loaded;
    private string _query = "";
    private IReadOnlyList<SoundRow> _results = [];
    private int _selectedIndex = -1;
    private string _status = "";
    private bool _statusIsError;

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? ""))
                Refilter(keepSelection: false);
        }
    }

    public IReadOnlyList<SoundRow> Results
    {
        get => _results;
        private set => Set(ref _results, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Set(ref _selectedIndex, value);
    }

    public SoundRow? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    public string Heading => Query.Trim().Length == 0
        ? "RECENTLY PLAYED"
        : Results.Count == 1 ? "1 MATCH" : $"{Results.Count} MATCHES";

    public bool ShowNoMatches => _loaded && Results.Count == 0;

    public string NoMatchesText => _library.Count == 0
        ? "Soundpad's list is empty."
        : $"Nothing matches \"{Query.Trim()}\".";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    public void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }

    /// <summary>New sound list from Soundpad. Keeps the highlighted sound if it is still there.</summary>
    public void SetLibrary(SoundSearch library)
    {
        _library = library;
        _loaded = true;
        Refilter(keepSelection: true);
    }

    public SoundEntry? Find(string url) => _library.FindByUrl(url);

    public void Move(int delta)
    {
        if (Results.Count == 0)
            return;
        int from = SelectedIndex < 0 ? (delta > 0 ? -1 : Results.Count) : SelectedIndex;
        SelectedIndex = Math.Clamp(from + delta, 0, Results.Count - 1);
    }

    private void Refilter(bool keepSelection)
    {
        string? keepUrl = keepSelection ? Selected?.Url : null;
        var rows = _library.Search(Query).Select(e => new SoundRow(e)).ToList();
        Results = rows;
        int index = keepUrl is null
            ? -1
            : rows.FindIndex(r => string.Equals(r.Url, keepUrl, StringComparison.OrdinalIgnoreCase));
        SelectedIndex = index >= 0 ? index : (rows.Count > 0 ? 0 : -1);
        Raise(nameof(Heading));
        Raise(nameof(ShowNoMatches));
        Raise(nameof(NoMatchesText));
    }
}
