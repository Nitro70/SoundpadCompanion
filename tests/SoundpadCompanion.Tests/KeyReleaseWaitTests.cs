using SoundpadCompanion.Overlay;
using Xunit;

namespace SoundpadCompanion.Tests;

/// <summary>
/// The overlay hands focus back to the game only once no key is held, so the Enter or Esc that
/// closed it never reaches the game. The keyboard here is a fake: a flag the test flips.
/// </summary>
public class KeyReleaseWaitTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void Runs_straight_away_when_no_key_is_down()
    {
        WpfThread.Run(() =>
        {
            var wait = new KeyReleaseWait(() => false, Poll, TimeSpan.FromSeconds(5));
            int ran = 0;
            wait.Start(() => ran++);
            Assert.Equal(1, ran);
            Assert.False(wait.IsWaiting);
        });
    }

    [Fact]
    public void Waits_while_a_key_is_held_and_runs_once_it_is_released()
    {
        WpfThread.Run(() =>
        {
            bool held = true;
            var wait = new KeyReleaseWait(() => held, Poll, TimeSpan.FromSeconds(5));
            int ran = 0;
            wait.Start(() => ran++);

            WpfThread.PumpUntil(() => ran > 0, TimeSpan.FromMilliseconds(200));
            Assert.Equal(0, ran);
            Assert.True(wait.IsWaiting);

            held = false;
            WpfThread.PumpUntil(() => ran > 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1, ran);
            Assert.False(wait.IsWaiting);

            WpfThread.PumpUntil(() => ran > 1, TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, ran);
        });
    }

    [Fact]
    public void Gives_up_waiting_after_the_limit()
    {
        WpfThread.Run(() =>
        {
            // A key that never reads as released (a stuck key, remapping software) must not
            // leave the overlay invisible and holding focus for ever.
            var wait = new KeyReleaseWait(() => true, Poll, TimeSpan.FromMilliseconds(150));
            int ran = 0;
            wait.Start(() => ran++);
            WpfThread.PumpUntil(() => ran > 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1, ran);
            Assert.False(wait.IsWaiting);
        });
    }

    [Fact]
    public void Cancel_stops_the_pending_action()
    {
        WpfThread.Run(() =>
        {
            bool held = true;
            var wait = new KeyReleaseWait(() => held, Poll, TimeSpan.FromSeconds(5));
            int ran = 0;
            wait.Start(() => ran++);
            wait.Cancel();
            held = false;
            WpfThread.PumpUntil(() => ran > 0, TimeSpan.FromMilliseconds(200));
            Assert.Equal(0, ran);
            Assert.False(wait.IsWaiting);
        });
    }

    [Fact]
    public void Starting_again_replaces_the_pending_action()
    {
        WpfThread.Run(() =>
        {
            bool held = true;
            var wait = new KeyReleaseWait(() => held, Poll, TimeSpan.FromSeconds(5));
            var ran = new List<string>();
            wait.Start(() => ran.Add("first"));
            wait.Start(() => ran.Add("second"));
            held = false;
            WpfThread.PumpUntil(() => ran.Count > 0, TimeSpan.FromSeconds(2));
            WpfThread.PumpUntil(() => ran.Count > 1, TimeSpan.FromMilliseconds(100));
            Assert.Equal(["second"], ran);
        });
    }

    [Fact]
    public void An_action_that_starts_a_new_wait_keeps_it()
    {
        WpfThread.Run(() =>
        {
            bool held = true;
            var wait = new KeyReleaseWait(() => held, Poll, TimeSpan.FromSeconds(5));
            int second = 0;
            wait.Start(() => { held = true; wait.Start(() => second++); });
            held = false;
            WpfThread.PumpUntil(() => wait.IsWaiting && held, TimeSpan.FromSeconds(2));
            Assert.True(wait.IsWaiting);
            held = false;
            WpfThread.PumpUntil(() => second > 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1, second);
        });
    }
}
