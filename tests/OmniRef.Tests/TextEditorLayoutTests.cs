using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OmniRef.Tests;

public sealed class TextEditorLayoutTests
{
    private const double EditorWidth = 500;
    private const double EditorHeight = 116;

    [Fact]
    public void TextBoxStyle_KeepsVerticalScrollBarWithinEditorBounds()
    {
        Exception? failure = null;
        Rect scrollBarBounds = Rect.Empty;
        Rect trackBounds = Rect.Empty;
        Rect thumbBounds = Rect.Empty;
        var thread = new Thread(
            () =>
            {
                try
                {
                    var resources = (ResourceDictionary)Application.LoadComponent(
                        new Uri("/OmniRef;component/Themes/Styles.xaml", UriKind.Relative));
                    var editor = new TextBox
                    {
                        Text = "abc\n123\n456\n789\nabc\n456",
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Padding = new Thickness(0),
                        FontSize = 72,
                        Width = EditorWidth,
                        Height = EditorHeight
                    };
                    editor.Resources.MergedDictionaries.Add(resources);
                    editor.Style = (Style)resources[typeof(TextBox)];
                    editor.Resources[typeof(ScrollBar)] = resources["TextEditorScrollBar"];

                    editor.Measure(new Size(editor.Width, editor.Height));
                    editor.Arrange(new Rect(0, 0, editor.Width, editor.Height));
                    editor.ApplyTemplate();
                    editor.UpdateLayout();
                    editor.Select(editor.Text.Length, 0);
                    editor.ScrollToEnd();
                    editor.UpdateLayout();

                    var scrollBar = FindVisualDescendants<ScrollBar>(editor)
                        .Single(candidate => candidate.Orientation == Orientation.Vertical);
                    scrollBar.Value = scrollBar.Maximum;
                    editor.UpdateLayout();
                    var topLeft = scrollBar.TranslatePoint(new Point(0, 0), editor);
                    scrollBarBounds = new Rect(
                        topLeft,
                        new Size(scrollBar.ActualWidth, scrollBar.ActualHeight));
                    var track = FindVisualDescendants<Track>(scrollBar).Single();
                    var trackTopLeft = track.TranslatePoint(new Point(0, 0), editor);
                    trackBounds = new Rect(
                        trackTopLeft,
                        new Size(track.ActualWidth, track.ActualHeight));
                    var thumb = FindVisualDescendants<Thumb>(scrollBar).Single();
                    var thumbTopLeft = thumb.TranslatePoint(new Point(0, 0), editor);
                    thumbBounds = new Rect(
                        thumbTopLeft,
                        new Size(thumb.ActualWidth, thumb.ActualHeight));
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(scrollBarBounds.Left >= 0, $"Scrollbar starts at {scrollBarBounds.Left}.");
        Assert.True(scrollBarBounds.Top >= 2, $"Scrollbar starts at {scrollBarBounds.Top}.");
        Assert.True(scrollBarBounds.Width > 0, "Scrollbar was not laid out.");
        Assert.True(scrollBarBounds.Height > 0, "Scrollbar was not laid out.");
        Assert.True(
            scrollBarBounds.Right <= EditorWidth - 2,
            $"Scrollbar ends at {scrollBarBounds.Right}.");
        Assert.True(
            scrollBarBounds.Bottom <= EditorHeight - 2,
            $"Scrollbar ends at {scrollBarBounds.Bottom}.");
        Assert.True(
            thumbBounds.Left >= trackBounds.Left,
            $"Thumb starts at {thumbBounds.Left}, before the track at {trackBounds.Left}.");
        Assert.True(
            thumbBounds.Top >= trackBounds.Top,
            $"Thumb starts at {thumbBounds.Top}, before the track at {trackBounds.Top}.");
        Assert.True(
            thumbBounds.Right <= trackBounds.Right,
            $"Thumb ends at {thumbBounds.Right}, past the track at {trackBounds.Right}.");
        Assert.True(
            thumbBounds.Bottom <= trackBounds.Bottom,
            $"Thumb ends at {thumbBounds.Bottom}, past the track at {trackBounds.Bottom}.");
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
