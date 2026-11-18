using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoundpadCompanion.Config;
using SoundpadCompanion.Hotkeys;
using SoundpadCompanion.Import;
using SoundpadCompanion.Overlay;
using SoundpadCompanion.Settings;
using SoundpadCompanion.Soundpad;

namespace SoundpadCompanion;

/// <summary>
/// A tray app: no window at start (except Settings on the very first run). Owns the hotkey,
/// the overlay, the folder importer and the tray icon.
/// </summary>
public partial class App : Application, ISettingsHost
{
    private const string InstanceName = @"Local\SoundpadCompanion.Instance";
    private const string ShowSignalName = @"Local\SoundpadCompanion.ShowSettings";

    private Mutex? _instance;
    private bool _ownsInstance;
    private EventWaitHandle? _showSignal;
    private volatile bool _exiting;
    private ConfigStore _store = null!;
    private ActivityLog _log = null!;
    private GlobalHotkey? _hotkey;
    private OverlayWindow? _overlay;
    private FolderImporter? _importer;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;

    public AppConfig Config { get; private set; } = new();
    public SoundpadApi Api { get; } = new(new SoundpadPipe());
    private MicBoost Boost => _boost ??= new MicBoost(Api, new SoundpadProcessMemory(), SoundpadRegistry.ReadAudioSettings,
        startLimit: TimeSpan.FromSeconds(1.5), hold: TimeSpan.FromSeconds(0.4));
    private MicBoost? _boost;
    public string? HotkeyProblem { get; private set; }
    public ObservableCollection<string> LogEntries => _log.Entries;
    IActivityLog ISettingsHost.Log => _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
        {
            RunSelfTest(selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1] : null);
            return;
        }

        _instance = new Mutex(initiallyOwned: true, InstanceName, out _ownsInstance);
        if (!_ownsInstance)
        {
            // Already running: ask that copy to show its Settings, then leave.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundpadCompanion");
        _log = new ActivityLog(Dispatcher, Path.Combine(dataDir, "activity.log"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Add($"Background error: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };

        _store = new ConfigStore(dataDir);
        (Config, bool firstRun) = _store.Load(_log);

        RecoverMicBoost();
        _overlay = new OverlayWindow(Api, Boost, () => Config, _log);
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += () => _overlay.Toggle();
        RegisterConfiguredHotkey();

        _importer = new FolderImporter(Api, _log, new ImportState(Path.Combine(dataDir, "import-state.json")));
        _importer.Apply(Config.ImportRules, catchUp: Config.CatchUpOnStart);

        _tray = new TrayIcon(OpenOverlay, ShowSettings, Quit);
        _tray.Tooltip = $"Soundpad Companion ({Config.Hotkey} to search)";

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(ListenForShowSignal) { IsBackground = true, Name = "Show settings signal" }.Start();

        _log.Add($"Started. {Config.Hotkey} opens the search.");
        if (firstRun)
            SaveConfig();
        if ((firstRun && !e.Args.Contains("--tray")) || e.Args.Contains("--settings"))
            ShowSettings();
    }

    // ---- ISettingsHost ----

    public void SaveConfig()
    {
        try
        {
            _store.Save(Config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Add($"Couldn't save settings: {e.Message}");
        }
    }

    public string? ChangeHotkey(Hotkey hotkey)
    {
        string? problem = _hotkey!.Register(hotkey);
        if (problem is null)
        {
            Config.Hotkey = hotkey.ToString();
            HotkeyProblem = null;
            SaveConfig();
            if (_tray is not null)
                _tray.Tooltip = $"Soundpad Companion ({Config.Hotkey} to search)";
            _log.Add($"Hotkey changed to {hotkey}");
        }
        return problem;
    }

    public void SuspendHotkey() => _hotkey?.Unregister();

    public void ResumeHotkey()
    {
        if (_hotkey is { Current: null })
            RegisterConfiguredHotkey();
    }

    public void ApplyImportRules() => _importer?.Apply(Config.ImportRules, catchUp: false);

    public void ImportExisting(ImportRule rule) => _importer?.ImportExisting(rule);

    public void OpenOverlay() => _overlay?.ShowOverlay();

    // ---- internals ----

    private void RegisterConfiguredHotkey()
    {
        if (!Hotkey.TryParse(Config.Hotkey, out var hotkey))
        {
            _log.Add($"The saved hotkey \"{Config.Hotkey}\" isn't valid; using {Hotkey.Default}.");
            hotkey = Hotkey.Default;
            Config.Hotkey = hotkey.ToString();
            SaveConfig();
        }
        HotkeyProblem = _hotkey!.Register(hotkey);
        if (HotkeyProblem is not null)
            _log.Add(HotkeyProblem);
    }

    private void ShowSettings()
    {
        if (_exiting)
            return;
        if (_settings is null)
        {
            _settings = new SettingsWindow(new SettingsViewModel(this));
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized)
            _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private void ListenForShowSignal()
    {
        try
        {
            while (_showSignal!.WaitOne())
            {
                if (_exiting)
                    return;
                Dispatcher.BeginInvoke(ShowSettings);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void Quit()
    {
        if (_exiting)
            return;
        _exiting = true;
        _settings?.Close();
        _importer?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _overlay?.Close();
        _showSignal?.Set();   // lets the listener thread see _exiting and stop
        Shutdown();
    }

    /// <summary>
    /// <c>--selftest [report.txt]</c>: builds the windows (never shown), loads the theme and icons and
    /// asks Soundpad read-only questions, then exits 0 if everything loaded. No tray icon, no hotkey,
    /// no settings written. Proves a published single-file exe is whole.
    /// </summary>
    private async void RunSelfTest(string? reportPath)
    {
        var report = new List<string>();
        int failures = 0;
        void Check(string what, Func<string> step)
        {
            try { report.Add($"ok    {what}: {step()}"); }
            catch (Exception ex) { failures++; report.Add($"FAIL  {what}: {ex.GetType().Name}: {ex.Message}"); }
        }

        _log = new ActivityLog(Dispatcher, file: null);
        Check("theme", () => Resources["AccentBrush"]?.ToString() ?? throw new InvalidOperationException("missing"));
        Check("window icon", () => AppIcon.ImageSource?.ToString() ?? throw new InvalidOperationException("missing"));
        Check("tray icon", () => { using var icon = AppIcon.LoadTrayIcon(); return $"{icon.Width}x{icon.Height}"; });
        Check("overlay window", () => { var w = new OverlayWindow(Api, Boost, () => Config, _log); w.Close(); return "built"; });
        Check("settings view", () => { _ = new SettingsView(); return "built"; });
        try
        {
            bool alive = await Api.IsAliveAsync();
            report.Add(alive
                ? $"ok    soundpad: {await Api.GetVersionAsync()}, {await Api.GetSoundCountAsync()} sounds"
                : "info  soundpad: not running");
        }
        catch (Exception ex)
        {
            failures++;
            report.Add($"FAIL  soundpad: {ex.Message}");
        }

        try
        {
            report.Add($"ok    mic boost: {Boost.Describe()}");   // read-only: nothing is written
        }
        catch (MicBoostException ex)
        {
            report.Add($"info  mic boost: {ex.Message}");
        }

        report.Add(failures == 0 ? "PASS" : $"{failures} FAILED");
        if (reportPath is not null)
        {
            try { File.WriteAllLines(reportPath, report); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>If the app was closed in the middle of a +30 dB boost, Soundpad is still at +30: put it back.</summary>
    private void RecoverMicBoost()
    {
        try
        {
            if (Boost.RecoverIfStuck() is { } fixedIt)
                _log.Add(fixedIt);
        }
        catch (MicBoostException e)
        {
            _log.Add($"+30 dB boost check at start: {e.Message}");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray app alive; the log says what happened.
        _log.Add($"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance)
        {
            try { _instance?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _instance?.Dispose();
        base.OnExit(e);
    }
}
