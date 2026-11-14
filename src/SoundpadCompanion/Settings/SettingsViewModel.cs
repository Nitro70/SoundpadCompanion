using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundpadCompanion.Config;
using SoundpadCompanion.Hotkeys;
using SoundpadCompanion.Soundpad;
using SoundpadCompanion.Ui;

namespace SoundpadCompanion.Settings;

/// <summary>What the settings window needs from the running app.</summary>
public interface ISettingsHost
{
    AppConfig Config { get; }
    ObservableCollection<string> LogEntries { get; }
    SoundpadApi Api { get; }
    IActivityLog Log { get; }

    void SaveConfig();

    /// <summary>Registers and saves a new hotkey. Returns null on success, else why it failed.</summary>
    string? ChangeHotkey(Hotkey hotkey);

    /// <summary>Why the current hotkey isn't registered, or null if it is.</summary>
    string? HotkeyProblem { get; }

    /// <summary>Releases the hotkey while the capture box is focused, so the old combination can be typed in.</summary>
    void SuspendHotkey();

    void ResumeHotkey();

    void ApplyImportRules();

    void ImportExisting(ImportRule rule);

    void OpenOverlay();
}

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsHost _host;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _applyTimer;
    private bool _connected;
    private string _connectionText = "Checking Soundpad…";
    private string _hotkeyText;
    private string _hotkeyMessage = "";
    private bool _hotkeyFailed;
    private bool _capturing;
    private bool _checking;

    public SettingsViewModel(ISettingsHost host)
    {
        _host = host;
        _hotkeyText = host.Config.Hotkey;
        ShowHotkeyProblem(host.HotkeyProblem);

        foreach (var rule in host.Config.ImportRules)
            Rules.Add(NewRuleViewModel(rule));

        AddFolderCommand = new RelayCommand(AddFolder);
        TryOverlayCommand = new RelayCommand(host.OpenOverlay);

        // Rule edits (typing a category name...) are saved and applied once typing pauses.
        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            _host.SaveConfig();
            _host.ApplyImportRules();
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();
        _ = RefreshStatusAsync();
    }

    public ObservableCollection<ImportRuleViewModel> Rules { get; } = [];

    /// <summary>Category names for the pickers (Soundpad's own categories, system ones left out).</summary>
    public ObservableCollection<string> CategoryNames { get; } = [];

    public ObservableCollection<string> Log => _host.LogEntries;

    public ICommand AddFolderCommand { get; }
    public ICommand TryOverlayCommand { get; }

    public bool Connected
    {
        get => _connected;
        private set => Set(ref _connected, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    public string HotkeyText
    {
        get => _hotkeyText;
        private set => Set(ref _hotkeyText, value);
    }

    public string HotkeyMessage
    {
        get => _hotkeyMessage;
        private set => Set(ref _hotkeyMessage, value);
    }

    public bool HotkeyFailed
    {
        get => _hotkeyFailed;
        private set => Set(ref _hotkeyFailed, value);
    }

    public bool CloseAfterPlay
    {
        get => _host.Config.CloseAfterPlay;
        set
        {
            _host.Config.CloseAfterPlay = value;
            Raise();
            _host.SaveConfig();
        }
    }

    public bool CatchUpOnStart
    {
        get => _host.Config.CatchUpOnStart;
        set
        {
            _host.Config.CatchUpOnStart = value;
            Raise();
            _host.SaveConfig();
        }
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            try
            {
                StartupRegistration.Set(value);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                _host.Log.Add($"Couldn't change Start with Windows: {e.Message}");
            }
            Raise();
        }
    }

    public void BeginHotkeyCapture()
    {
        if (_capturing)
            return;
        _capturing = true;
        _host.SuspendHotkey();
        HotkeyText = "Press the new combination…";
        HotkeyMessage = "Esc to cancel.";
        HotkeyFailed = false;
    }

    public void EndHotkeyCapture()
    {
        if (!_capturing)
            return;
        _capturing = false;
        _host.ResumeHotkey();
        HotkeyText = _host.Config.Hotkey;
        ShowHotkeyProblem(_host.HotkeyProblem);
    }

    /// <returns>True when the combination was taken (the capture is over).</returns>
    public bool TryCaptureHotkey(Hotkey hotkey)
    {
        if (Hotkey.IsModifierKey(hotkey.Key))
            return false;   // still holding modifiers, wait for the real key
        if (!hotkey.IsValid)
        {
            HotkeyMessage = $"{hotkey} needs Ctrl, Alt, Shift or Win with it.";
            HotkeyFailed = true;
            return false;
        }

        string? error = _host.ChangeHotkey(hotkey);
        _capturing = false;
        HotkeyText = _host.Config.Hotkey;
        if (error is null)
        {
            ShowHotkeyProblem(null);
        }
        else
        {
            _host.ResumeHotkey();   // put the old one back
            HotkeyMessage = error;
            HotkeyFailed = true;
        }
        return true;
    }

    private void ShowHotkeyProblem(string? problem)
    {
        HotkeyFailed = problem is not null;
        HotkeyMessage = problem ?? "Works anywhere, including over games in borderless or windowed mode.";
    }

    private void AddFolder()
    {
        string? folder = PickFolder(null);
        if (folder is null)
            return;
        var rule = new ImportRule { Folder = folder };
        _host.Config.ImportRules.Add(rule);
        Rules.Add(NewRuleViewModel(rule));
        _host.SaveConfig();
        _host.ApplyImportRules();
    }

    private ImportRuleViewModel NewRuleViewModel(ImportRule rule) => new(
        rule,
        onChanged: () =>
        {
            _applyTimer.Stop();
            _applyTimer.Start();
        },
        onRemove: vm =>
        {
            _host.Config.ImportRules.Remove(vm.Model);
            Rules.Remove(vm);
            _host.SaveConfig();
            _host.ApplyImportRules();
        },
        onImportExisting: vm =>
        {
            // Make sure pending edits (category name...) are in force first.
            if (_applyTimer.IsEnabled)
            {
                _applyTimer.Stop();
                _host.SaveConfig();
                _host.ApplyImportRules();
            }
            _host.ImportExisting(vm.Model);
        },
        pickFolder: PickFolder);

    internal static string? PickFolder(string? start)
    {
        var dialog = new OpenFolderDialog { Title = "Folder to watch for new sounds" };
        if (!string.IsNullOrWhiteSpace(start) && System.IO.Directory.Exists(start))
            dialog.InitialDirectory = start;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private async Task RefreshStatusAsync()
    {
        if (_checking)
            return;
        _checking = true;
        try
        {
            var api = _host.Api;
            string version = await api.GetVersionAsync();
            int count = await api.GetSoundCountAsync();
            Connected = true;
            ConnectionText = $"Soundpad {version}  ·  {count} sounds";

            var names = (await api.GetCategoriesAsync(withSounds: false))
                .Where(c => !c.IsSystem)
                .Select(c => c.Path)
                .ToList();
            if (!names.SequenceEqual(CategoryNames))
            {
                CategoryNames.Clear();
                foreach (string name in names)
                    CategoryNames.Add(name);
            }
        }
        catch (SoundpadException)
        {
            Connected = false;
            ConnectionText = "Soundpad isn't running";
        }
        catch (System.Xml.XmlException)
        {
            Connected = true;
            ConnectionText = "Soundpad is running (couldn't read its categories)";
        }
        finally
        {
            _checking = false;
        }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        if (_applyTimer.IsEnabled)
        {
            _applyTimer.Stop();
            _host.SaveConfig();
            _host.ApplyImportRules();
        }
        if (_capturing)
            EndHotkeyCapture();
    }
}

public sealed class ImportRuleViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public ImportRuleViewModel(ImportRule model, Action onChanged, Action<ImportRuleViewModel> onRemove,
        Action<ImportRuleViewModel> onImportExisting, Func<string?, string?> pickFolder)
    {
        Model = model;
        _onChanged = onChanged;
        RemoveCommand = new RelayCommand(() => onRemove(this));
        ImportExistingCommand = new RelayCommand(() => onImportExisting(this));
        BrowseCommand = new RelayCommand(() =>
        {
            if (pickFolder(Folder) is { } folder)
                Folder = folder;
        });
    }

    public ImportRule Model { get; }

    public ICommand RemoveCommand { get; }
    public ICommand ImportExistingCommand { get; }
    public ICommand BrowseCommand { get; }

    public string Folder
    {
        get => Model.Folder;
        set
        {
            if (Model.Folder == value)
                return;
            Model.Folder = value;
            Raise();
            _onChanged();
        }
    }

    /// <summary>Soundpad can't take a double quote in a command, so they are dropped.</summary>
    public string Category
    {
        get => Model.Category;
        set
        {
            string clean = (value ?? "").Replace("\"", "");
            if (Model.Category == clean)
                return;
            Model.Category = clean;
            Raise();
            _onChanged();
        }
    }

    public bool IncludeSubfolders
    {
        get => Model.IncludeSubfolders;
        set
        {
            Model.IncludeSubfolders = value;
            Raise();
            _onChanged();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            Model.Enabled = value;
            Raise();
            _onChanged();
        }
    }
}
