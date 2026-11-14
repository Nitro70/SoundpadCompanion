using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Xml;
using SoundpadCompanion.Config;
using SoundpadCompanion.Search;
using SoundpadCompanion.Soundpad;

namespace SoundpadCompanion.Overlay;

/// <summary>
/// The pop-up search box. Created once at start-up and shown/hidden from then on, so the hotkey
/// opens it instantly. It refreshes the sound list from Soundpad every time it opens.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly SoundpadApi _api;
    private readonly Func<AppConfig> _config;
    private readonly IActivityLog _log;

    private IntPtr _returnFocusTo;
    private Task _refresh = Task.CompletedTask;
    private int _refreshGeneration;
    private bool _hiding;
    private bool _playing;

    public OverlayViewModel Vm { get; } = new();

    public OverlayWindow(SoundpadApi api, Func<AppConfig> config, IActivityLog log)
    {
        _api = api;
        _config = config;
        _log = log;
        InitializeComponent();
        DataContext = Vm;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => HideOverlay(restoreFocus: false);
        View.RowClicked += (row, modifiers) => _ = PlayAsync(row, TargetFor(modifiers));
        // A new search keeps the old scroll offset otherwise; bring the highlighted row into view.
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.SelectedIndex) && Vm.Selected is { } row)
                View.ResultsList.ScrollIntoView(row);
        };
    }

    public void Toggle()
    {
        if (IsVisible)
            HideOverlay(restoreFocus: true);
        else
            ShowOverlay();
    }

    public void ShowOverlay()
    {
        IntPtr self = new WindowInteropHelper(this).EnsureHandle();
        IntPtr foreground = Native.GetForegroundWindow();
        if (!IsVisible)
            _returnFocusTo = foreground == self ? IntPtr.Zero : foreground;

        PlaceOnMonitorOf(self, _returnFocusTo);
        Show();
        PlaceOnMonitorOf(self, _returnFocusTo);   // again: a DPI change on the way can resize it
        BringToFront(self);
        Activate();
        View.SearchBox.Focus();
        Keyboard.Focus(View.SearchBox);
        View.SearchBox.SelectAll();   // last search stays, selected: type to replace it, Enter to replay
        _refresh = RefreshAsync();
    }

    /// <param name="restoreFocus">Hand focus back to the window that was in front before (the game).</param>
    public void HideOverlay(bool restoreFocus)
    {
        if (_hiding || !IsVisible)
            return;
        _hiding = true;
        try
        {
            // Give focus back while we still hold the foreground; once hidden, Windows may refuse.
            if (restoreFocus && _returnFocusTo != IntPtr.Zero && Native.IsWindow(_returnFocusTo))
                Native.SetForegroundWindow(_returnFocusTo);
            Hide();
        }
        finally
        {
            _hiding = false;
        }
    }

    private async Task RefreshAsync()
    {
        int generation = ++_refreshGeneration;
        Vm.SetStatus("Loading…", isError: false);
        try
        {
            var sounds = await _api.GetSoundsAsync();
            var categories = await _api.GetCategoriesAsync(withSounds: true);
            if (generation != _refreshGeneration)
                return;
            Vm.SetLibrary(new SoundSearch(sounds, categories));
            Vm.SetStatus(sounds.Count == 1 ? "1 sound" : $"{sounds.Count} sounds", isError: false);
        }
        catch (SoundpadOfflineException e)
        {
            if (generation == _refreshGeneration)
                Vm.SetStatus(e.Message, isError: true);
        }
        catch (Exception e) when (e is SoundpadException or XmlException)
        {
            if (generation != _refreshGeneration)
                return;
            Vm.SetStatus("Couldn't read Soundpad's list", isError: true);
            _log.Add($"Search couldn't read Soundpad's list: {e.Message}");
        }
    }

    /// <summary>Same rule for Enter and for a click: Shift is speakers only, Ctrl is mic only.</summary>
    internal static PlayTarget TargetFor(ModifierKeys modifiers) =>
        modifiers.HasFlag(ModifierKeys.Shift) ? PlayTarget.SpeakersOnly
        : modifiers.HasFlag(ModifierKeys.Control) ? PlayTarget.MicOnly
        : PlayTarget.SoundpadDefault;

    private async Task PlayAsync(SoundRow row, PlayTarget target)
    {
        if (_playing)
            return;
        _playing = true;
        try
        {
            // Indexes shift when sounds are added or removed, so wait for the fresh list and
            // play the sound by its current index, found by file path.
            await _refresh;
            if (Vm.Find(row.Url) is not { } current)
            {
                Vm.SetStatus("That sound isn't in Soundpad any more", isError: true);
                return;
            }

            await _api.PlayAsync(current.Sound.Index, target);
            if (_config().CloseAfterPlay)
                HideOverlay(restoreFocus: true);
            else
                Vm.SetStatus($"Playing {current.Sound.DisplayTitle}", isError: false);
        }
        catch (SoundpadOfflineException e)
        {
            Vm.SetStatus(e.Message, isError: true);
        }
        catch (SoundpadCommandException e)
        {
            Vm.SetStatus($"Soundpad refused it ({e.Reply})", isError: true);
            _log.Add(e.Message);
        }
        finally
        {
            _playing = false;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideOverlay(restoreFocus: true);
                break;
            case Key.Down:
                Step(1);
                break;
            case Key.Up:
                Step(-1);
                break;
            case Key.PageDown:
                Step(8);
                break;
            case Key.PageUp:
                Step(-8);
                break;
            case Key.Enter:
                if (Vm.Selected is { } row)
                    _ = PlayAsync(row, TargetFor(Keyboard.Modifiers));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Step(int delta) => Vm.Move(delta);

    /// <summary>Centres the overlay horizontally, a little below the top, on the monitor of <paramref name="anchor"/>.</summary>
    private static void PlaceOnMonitorOf(IntPtr self, IntPtr anchor)
    {
        IntPtr monitor = Native.MonitorFromWindow(anchor != IntPtr.Zero ? anchor : self, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(self, out var rect))
            return;
        var work = info.rcWork;
        int x = work.Left + (work.Width - rect.Width) / 2;
        int y = work.Top + work.Height / 6;
        Native.SetWindowPos(self, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Takes keyboard focus from the game. A hotkey normally earns the right to do this; if Windows
    /// still says no, briefly join the foreground thread's input queue, which it allows.
    /// </summary>
    private static void BringToFront(IntPtr self)
    {
        if (Native.SetForegroundWindow(self) && Native.GetForegroundWindow() == self)
            return;
        IntPtr foreground = Native.GetForegroundWindow();
        uint theirs = Native.GetWindowThreadProcessId(foreground, out _);
        uint ours = Native.GetCurrentThreadId();
        if (theirs == 0 || theirs == ours || !Native.AttachThreadInput(ours, theirs, true))
            return;
        try
        {
            Native.BringWindowToTop(self);
            Native.SetForegroundWindow(self);
        }
        finally
        {
            Native.AttachThreadInput(ours, theirs, false);
        }
    }
}
