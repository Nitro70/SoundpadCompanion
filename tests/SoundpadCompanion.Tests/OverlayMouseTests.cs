using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundpadCompanion.Overlay;
using SoundpadCompanion.Search;
using SoundpadCompanion.Soundpad;
using Xunit;

namespace SoundpadCompanion.Tests;

/// <summary>
/// Fires real WPF mouse events (the same routed events Windows input produces) at the overlay's
/// rows, text and search box, laid out but never shown.
/// </summary>
public class OverlayMouseTests
{
    private sealed record Setup(OverlayView View, OverlayViewModel Vm, List<(SoundRow Row, ModifierKeys Mods)> Clicks);

    private static Setup Build()
    {
        var api = new SoundpadApi(RenderTests.SampleSoundpad(extra: 40));
        var vm = new OverlayViewModel();
        vm.SetLibrary(new SoundSearch(api.GetSoundsAsync().GetAwaiter().GetResult(), api.GetCategoriesAsync(true).GetAwaiter().GetResult()));
        var view = new OverlayView { DataContext = vm };
        var clicks = new List<(SoundRow, ModifierKeys)>();
        view.RowClicked += (row, mods) => clicks.Add((row, mods));
        WpfThread.Layout(view);
        return new Setup(view, vm, clicks);
    }

    private static ListBoxItem Row(OverlayView view, int index) =>
        (ListBoxItem)view.ResultsList.ItemContainerGenerator.ContainerFromIndex(index);

    // The title text inside a row: a click lands on the text, not on the row's border.
    private static TextBlock TitleOf(ListBoxItem row) => OverlayView.FindChild<TextBlock>(row)!;

    // Real input: PreviewMouseDown tunnels down to the element under the mouse, then MouseDown bubbles
    // back up, one event object for both. Each element turns them into its own (direct)
    // PreviewMouseLeftButtonDown / MouseLeftButtonDown, which is what the overlay and ListBoxItem handle.
    private static MouseButtonEventArgs Press(UIElement on) => Send(on, UIElement.PreviewMouseDownEvent, UIElement.MouseDownEvent);

    private static MouseButtonEventArgs Release(UIElement on) => Send(on, UIElement.PreviewMouseUpEvent, UIElement.MouseUpEvent);

    private static MouseButtonEventArgs Send(UIElement on, RoutedEvent preview, RoutedEvent bubble)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = preview };
        on.RaiseEvent(args);
        args.RoutedEvent = bubble;
        on.RaiseEvent(args);
        return args;
    }

    // Like real input: one event object goes through the tunnelling phase, then the bubbling one,
    // so a Preview handler that marks it handled also stops the bubble.
    private static void Wheel(UIElement on, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        on.RaiseEvent(args);
        args.RoutedEvent = UIElement.MouseWheelEvent;
        on.RaiseEvent(args);
    }

    private static double Offset(OverlayView view) => OverlayView.FindChild<ScrollViewer>(view.ResultsList)!.VerticalOffset;

    [Fact]
    public void Clicking_a_row_highlights_it_and_plays_that_row()
    {
        WpfThread.Run(() =>
        {
            var (view, vm, clicks) = Build();
            var third = Row(view, 2);

            Press(TitleOf(third));
            Assert.Equal(2, vm.SelectedIndex);   // highlighted on press
            Release(TitleOf(third));

            // Which modifier was held comes from the real keyboard (someone typing with Shift down
            // changes it), so only the row is checked here; the Shift/Ctrl mapping has its own test.
            var click = Assert.Single(clicks);
            Assert.Same(third.DataContext, click.Row);
        });
    }

    [Fact]
    public void Pressing_one_row_and_releasing_on_another_plays_nothing()
    {
        WpfThread.Run(() =>
        {
            var (view, _, clicks) = Build();
            Press(TitleOf(Row(view, 1)));
            Release(TitleOf(Row(view, 3)));
            Assert.Empty(clicks);
        });
    }

    [Fact]
    public void Clicks_do_not_take_focus_from_the_search_box()
    {
        WpfThread.Run(() =>
        {
            var (view, _, _) = Build();
            var press = Press(TitleOf(Row(view, 0)));
            Assert.True(press.Handled);   // the row never gets to run its own focus-taking click code
            Assert.False(Row(view, 0).IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public void The_wheel_scrolls_the_list_over_rows_and_over_the_search_box()
    {
        WpfThread.Run(() =>
        {
            var (view, _, _) = Build();
            Assert.Equal(0, Offset(view));

            Wheel(TitleOf(Row(view, 0)), -120);
            WpfThread.Layout(view);
            double afterRows = Offset(view);
            Assert.True(afterRows > 0, "wheel over a row did not scroll");

            Wheel(view.SearchBox, -120);
            WpfThread.Layout(view);
            Assert.True(Offset(view) > afterRows, "wheel over the search box did not scroll");
        });
    }

    [Fact]
    public void The_scroll_bar_is_not_a_row()
    {
        WpfThread.Run(() =>
        {
            var (view, _, clicks) = Build();
            var bar = OverlayView.FindChild<System.Windows.Controls.Primitives.ScrollBar>(view.ResultsList)!;
            Assert.Null(OverlayView.RowContainer(bar));
            Press(bar);
            Release(bar);
            Assert.Empty(clicks);
        });
    }

    [Theory]
    [InlineData(ModifierKeys.None, PlayTarget.SoundpadDefault)]
    [InlineData(ModifierKeys.Shift, PlayTarget.SpeakersOnly)]
    [InlineData(ModifierKeys.Control, PlayTarget.MicOnly)]
    public void Shift_and_Ctrl_mean_the_same_for_clicks_as_for_Enter(ModifierKeys mods, PlayTarget expected) =>
        Assert.Equal(expected, OverlayWindow.TargetFor(mods));
}
