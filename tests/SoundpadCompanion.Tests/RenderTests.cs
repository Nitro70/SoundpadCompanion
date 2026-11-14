using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoundpadCompanion.Config;
using SoundpadCompanion.Hotkeys;
using SoundpadCompanion.Overlay;
using SoundpadCompanion.Search;
using SoundpadCompanion.Settings;
using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

/// <summary>
/// Builds the overlay and settings views with their real XAML and bindings, lays them out and
/// renders them to PNG, without opening a window. Always checks that the XAML loads and binds;
/// writes the PNGs when SPC_RENDER_DIR is set (with SPC_LIVE=1, using the real Soundpad list).
/// </summary>
public class RenderTests
{
    [Fact]
    public void Overlay_and_settings_load_bind_and_render()
    {
        WpfThread.Run(() =>
        {
            bool live = Environment.GetEnvironmentVariable("SPC_LIVE") == "1";
            string? outDir = Environment.GetEnvironmentVariable("SPC_RENDER_DIR");
            ISoundpadTransport transport = live ? new SoundpadPipe() : SampleSoundpad();
            var api = new SoundpadApi(transport);

            var sounds = api.GetSoundsAsync().GetAwaiter().GetResult();
            var categories = api.GetCategoriesAsync(true).GetAwaiter().GetResult();
            var library = new SoundSearch(sounds, categories);

            // Overlay: recently played, then a search.
            var vm = new OverlayViewModel();
            vm.SetLibrary(library);
            vm.SetStatus($"{sounds.Count} sounds", false);
            var overlay = new OverlayView { DataContext = vm };
            Render(overlay, outDir, "overlay-recent.png");
            Assert.True(vm.Results.Count > 0);

            // Live: search the first word of the most played sound, so the shot has real hits.
            vm.Query = live
                ? sounds.OrderByDescending(s => s.PlayCount).First().DisplayTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                : "boom";
            vm.Move(1);
            Render(overlay, outDir, "overlay-search.png");

            vm.Query = "zzzzqqq";
            Render(overlay, outDir, "overlay-nomatch.png");
            Assert.True(vm.ShowNoMatches);

            // Settings with one import rule.
            var host = new FakeHost(api);
            host.Config.ImportRules.Add(new ImportRule { Folder = @"C:\Users\you\Downloads\New sounds", Category = "Memes" });
            host.LogEntries.Add("14:02:11  Added airhorn.mp3 to Memes");
            host.LogEntries.Add("14:01:40  Watching C:\\Users\\you\\Downloads\\New sounds");
            var settingsVm = new SettingsViewModel(host);
            WpfThread.PumpUntil(() => settingsVm.ConnectionText != "Checking Soundpad…", TimeSpan.FromSeconds(5));
            var settings = new SettingsView { DataContext = settingsVm, Width = 760 };
            Render(settings, outDir, "settings.png");
            Assert.True(settingsVm.Connected);
            settingsVm.Dispose();
        });
    }

    internal static void Render(FrameworkElement element, string? outDir, string name)
    {
        WpfThread.Layout(element);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        if (outDir is null)
            return;

        const double scale = 1.5;
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // Dark backdrop so the overlay's transparent shadow area reads like it would over a game.
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x36)), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        target.Render(backdrop);
        target.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(outDir);
        using var file = File.Create(Path.Combine(outDir, name));
        encoder.Save(file);
    }

    /// <summary>An invented library (also what the README screenshots show).</summary>
    internal static FakeSoundpad SampleSoundpad(int extra = 0)
    {
        (string Title, string Artist, string Duration)[] sounds =
        [
            ("Vine Boom", "", "0:01"),              // 1
            ("Boom Headshot", "", "0:03"),          // 2
            ("Airhorn", "", "0:02"),                // 3
            ("Bruh", "", "0:01"),                   // 4
            ("Sad Trombone", "", "0:04"),           // 5
            ("Drum Roll", "Studio Kit", "0:06"),    // 6
            ("Crowd Goes Wild", "Stadium Pack", "0:08"), // 7
            ("Boombox Intro", "Night Drive", "3:12"),    // 8
            ("Record Scratch", "", "0:01"),         // 9
            ("Dramatic Sting", "Studio Kit", "0:03"),    // 10
            ("Lofi Loop", "Night Drive", "2:40"),   // 11
            ("Wrong Answer Buzzer", "", "0:01"),    // 12
        ];
        var fake = new FakeSoundpad
        {
            Override = c => c == "GetVersion()" ? "4.0.35" : c == "GetSoundFileCount()" ? $"{sounds.Length + extra}" : null,
        };
        foreach (var (title, artist, duration) in sounds)
            fake.Sounds.Add(new FakeSoundpad.FakeSound($@"C:\Sounds\{title}.mp3", title, artist, duration));
        for (int i = 1; i <= extra; i++)
            fake.Sounds.Add(new FakeSoundpad.FakeSound($@"C:\Sounds\Extra {i}.mp3", $"Extra sound {i}"));
        fake.Categories.Add(("Memes", [1, 2, 3, 4, 5, 12]));
        fake.Categories.Add(("Effects", [6, 7, 9, 10]));
        fake.Categories.Add(("Songs", [8, 11]));
        return fake;
    }

    private sealed class FakeHost(SoundpadApi api) : ISettingsHost
    {
        public AppConfig Config { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = [];
        public SoundpadApi Api { get; } = api;
        public IActivityLog Log { get; } = new ListLog();
        public string? HotkeyProblem => null;
        public void SaveConfig() { }
        public string? ChangeHotkey(Hotkey hotkey) => null;
        public void SuspendHotkey() { }
        public void ResumeHotkey() { }
        public void ApplyImportRules() { }
        public void ImportExisting(ImportRule rule) { }
        public void OpenOverlay() { }
    }
}
