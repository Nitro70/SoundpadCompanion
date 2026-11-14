using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace SoundpadCompanion.Tests;

/// <summary>
/// One STA thread with a running dispatcher and the app's theme loaded, shared by every WPF test
/// (WPF allows a single Application per process). Nothing is ever shown on screen.
/// </summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    // One test at a time: a test that pumps the dispatcher would otherwise run the next queued test inside itself.
    private static readonly object Gate = new();

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        lock (Gate)
        {
            Ui.Value.Invoke(() =>
            {
                try { action(); }
                catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
            });
        }
        error?.Throw();
    }

    /// <summary>Lets queued dispatcher work (bindings, layout, async continuations) run.</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(20);
        }
    }

    public static void Layout(FrameworkElement element)
    {
        Pump();
        element.Measure(new Size(element.Width > 0 ? element.Width : double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        Pump();
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SoundpadCompanion;component/Theme/Dark.xaml"),
            });
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
