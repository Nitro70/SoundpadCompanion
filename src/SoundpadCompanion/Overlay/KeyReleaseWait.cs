using System.Diagnostics;
using System.Windows.Threading;

namespace SoundpadCompanion.Overlay;

/// <summary>
/// Runs an action once no key is held down, without blocking the UI thread.
/// <para>
/// The overlay uses it before handing focus back to the game. A key still down at that moment
/// (the Enter that played a sound, the Esc that closed the overlay, the hotkey, a Shift or Ctrl)
/// would reach the game: its release goes to whichever window is in front by then, and games
/// that read the keyboard when they get focus back see it held and take it as a fresh press.
/// </para>
/// </summary>
internal sealed class KeyReleaseWait
{
    private readonly Func<bool> _anyKeyDown;
    private readonly TimeSpan _limit;
    private readonly DispatcherTimer _timer;
    private Action? _then;
    private long _started;

    /// <param name="anyKeyDown">Reads the keyboard; <see cref="Native.AnyKeyDown"/> in the app.</param>
    /// <param name="poll">How often to look again while a key is held.</param>
    /// <param name="limit">Run anyway after this long, so a key that never reads as released can't hold things up for ever.</param>
    public KeyReleaseWait(Func<bool> anyKeyDown, TimeSpan poll, TimeSpan limit)
    {
        _anyKeyDown = anyKeyDown;
        _limit = limit;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = poll };
        _timer.Tick += (_, _) => Check();
    }

    public bool IsWaiting => _then != null;

    /// <summary>
    /// Runs <paramref name="then"/> once no key is down, straight away if none is now.
    /// Replaces any action still waiting.
    /// </summary>
    public void Start(Action then)
    {
        Cancel();
        if (!_anyKeyDown())
        {
            then();
            return;
        }
        _then = then;
        _started = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _then = null;
    }

    private void Check()
    {
        if (_then is not { } then)
            return;
        if (_anyKeyDown() && Stopwatch.GetElapsedTime(_started) < _limit)
            return;
        // Clear first: the action may start a new wait, which must survive.
        Cancel();
        then();
    }
}
