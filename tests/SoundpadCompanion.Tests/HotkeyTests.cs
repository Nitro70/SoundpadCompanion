using System.Windows.Input;
using SoundpadCompanion.Hotkeys;
using Xunit;

namespace SoundpadCompanion.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, Key.Space)]
    [InlineData("alt + f", HotkeyModifiers.Alt, Key.F)]
    [InlineData("Win+Control+5", HotkeyModifiers.Win | HotkeyModifiers.Ctrl, Key.D5)]
    [InlineData("F9", HotkeyModifiers.None, Key.F9)]
    public void Parses(string text, HotkeyModifiers modifiers, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]            // no modifier
    [InlineData("Ctrl+Shift")]       // no key
    [InlineData("Ctrl+A+B")]         // two keys
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+LeftShift")]   // a modifier as the key
    public void Rejects(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Round_trips_through_text()
    {
        var hotkey = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Key.D7);
        Assert.Equal("Ctrl+Alt+Shift+7", hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var back));
        Assert.Equal(hotkey, back);
        Assert.Equal("Ctrl+Shift+Space", Hotkey.Default.ToString());
    }

    [Fact]
    public void Alt_combinations_use_the_system_key()
    {
        var hotkey = Hotkey.FromKeyPress(Key.System, Key.S, ModifierKeys.Alt);
        Assert.Equal(new Hotkey(HotkeyModifiers.Alt, Key.S), hotkey);
    }
}
