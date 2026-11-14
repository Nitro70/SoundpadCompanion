using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SoundpadCompanion.Overlay;

/// <summary>
/// The overlay's content. Kept apart from the window so it can be built and rendered on its own.
/// <para>Rows are not focusable, so typing always stays in the search box. WPF's ListBoxItem only
/// reacts to a click when it can take focus, so clicks are handled here instead: press and release
/// on the same row is a click.</para>
/// </summary>
public partial class OverlayView : UserControl
{
    private ListBoxItem? _pressed;

    /// <summary>A row was clicked, with the modifier keys held at the time.</summary>
    public event Action<SoundRow, ModifierKeys>? RowClicked;

    public OverlayView()
    {
        InitializeComponent();
        ResultsList.PreviewMouseLeftButtonDown += OnRowMouseDown;
        ResultsList.PreviewMouseLeftButtonUp += OnRowMouseUp;
        PreviewMouseWheel += OnMouseWheelAnywhere;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = RowContainer(e.OriginalSource as DependencyObject);
        if (_pressed is null)
            return;   // the scroll bar or empty space: leave it to the list
        int index = ResultsList.ItemContainerGenerator.IndexFromContainer(_pressed);
        if (index >= 0)
            ResultsList.SelectedIndex = index;
        e.Handled = true;
    }

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        var released = RowContainer(e.OriginalSource as DependencyObject);
        if (released is null || released != pressed)
            return;
        e.Handled = true;
        if (released.DataContext is SoundRow row)
            RowClicked?.Invoke(row, Keyboard.Modifiers);
    }

    /// <summary>The wheel scrolls the results wherever the mouse is on the overlay, not only over the list.</summary>
    private void OnMouseWheelAnywhere(object sender, MouseWheelEventArgs e)
    {
        if (IsInside(e.OriginalSource as DependencyObject, ResultsList))
            return;   // the list scrolls itself
        if (FindChild<ScrollViewer>(ResultsList) is { } scroller)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 0.4);
            e.Handled = true;
        }
    }

    /// <summary>The row under an element, or null if it is not inside a row (scroll bar, padding).</summary>
    internal static ListBoxItem? RowContainer(DependencyObject? element)
    {
        for (var d = element; d is not null; d = ParentOf(d))
        {
            if (d is ListBoxItem item)
                return item;
            if (d is ListBox)
                return null;
        }
        return null;
    }

    private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = ParentOf(d))
        {
            if (d == ancestor)
                return true;
        }
        return false;
    }

    // Text runs inside a TextBlock are not visuals, so climb those through the logical tree.
    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }
}
