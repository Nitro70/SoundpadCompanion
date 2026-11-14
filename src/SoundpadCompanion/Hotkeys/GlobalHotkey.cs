using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SoundpadCompanion.Hotkeys;

/// <summary>One system-wide hotkey (RegisterHotKey) on a hidden message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 1;

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public Hotkey? Current { get; private set; }

    public GlobalHotkey()
    {
        var parameters = new HwndSourceParameters("SoundpadCompanionHotkey")
        {
            ParentWindow = Native.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <returns>null on success, otherwise a sentence saying why it failed.</returns>
    public string? Register(Hotkey hotkey)
    {
        Unregister();
        if (!hotkey.IsValid)
            return $"{hotkey} isn't usable as a hotkey: hold Ctrl, Alt, Shift or Win with another key.";

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!Native.RegisterHotKey(_source.Handle, Id, (uint)hotkey.Modifiers | Native.MOD_NOREPEAT, vk))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{hotkey} is already taken by another program. Pick another."
                : $"Windows refused {hotkey} (error {error}).";
        }

        _registered = true;
        Current = hotkey;
        return null;
    }

    public void Unregister()
    {
        if (_registered)
        {
            Native.UnregisterHotKey(_source.Handle, Id);
            _registered = false;
        }
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
