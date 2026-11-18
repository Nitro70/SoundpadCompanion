using System.Globalization;
using Microsoft.Win32;

namespace SoundpadCompanion.Soundpad;

/// <summary>Soundpad's own settings, read (never written) from where Soundpad saves them.</summary>
internal static class SoundpadRegistry
{
    private const string MainFrame = @"Software\Leppsoft\Soundpad\MainFrame";

    /// <summary>The Volume normalization settings, or null if Soundpad hasn't saved them (or saved something unexpected).</summary>
    public static SoundpadAudioSettings? ReadAudioSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(MainFrame);
        if (key?.GetValue("normalizeVolume") is not int mode
            || Float(key, "normalizeTargetDecibel") is not { } target
            || Float(key, "averageSoundfileDecibel") is not { } average
            || Float(key, "fixedVolumeAdjustmentDecibel") is not { } adjust)
            return null;
        return new SoundpadAudioSettings(mode, target, average, adjust);
    }

    private static float? Float(RegistryKey key, string name) =>
        key.GetValue(name) is string text && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;
}
