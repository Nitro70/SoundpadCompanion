using System.Windows.Input;

namespace SoundpadCompanion.Hotkeys;

/// <summary>Values match RegisterHotKey's MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A key combination such as Ctrl+Shift+Space, stored as text in the config.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, Key Key)
{
    public static readonly Hotkey Default = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, Key.Space);

    /// <summary>A real key plus at least one modifier (F1 to F24 may stand alone).</summary>
    public bool IsValid =>
        Key != Key.None && !IsModifierKey(Key) && (Modifiers != HotkeyModifiers.None || IsFunctionKey(Key));

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = HotkeyModifiers.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Ctrl; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; continue;
            }
            if (key != Key.None || !TryParseKey(part, out key))
                return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }

    /// <summary>Builds a hotkey from a key press in a capture box.</summary>
    public static Hotkey FromKeyPress(Key key, Key systemKey, ModifierKeys modifiers)
    {
        // Alt combinations arrive as Key.System with the real key in SystemKey.
        Key real = key == Key.System ? systemKey : key;
        var mods = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return new Hotkey(mods, real);
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None && Enum.IsDefined(key);
    }
}
