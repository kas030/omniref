using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OmniRef.App.Controls;

public sealed class OverflowFadingTextBlock : Decorator
{
    private const double OverflowTolerance = 0.5;
    private static readonly Brush FadeMask = CreateFadeMask();

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(OverflowFadingTextBlock),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private readonly TextBlock _textBlock;
    private double _unconstrainedTextWidth;

    public OverflowFadingTextBlock()
    {
        _textBlock = new TextBlock();
        Child = _textBlock;
        ClipToBounds = true;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _textBlock.Measure(new Size(double.PositiveInfinity, constraint.Height));
        _unconstrainedTextWidth = _textBlock.DesiredSize.Width;

        _textBlock.Measure(constraint);
        return _textBlock.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        OpacityMask = _unconstrainedTextWidth > arrangeSize.Width + OverflowTolerance
            ? FadeMask
            : null;
        _textBlock.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    private static void OnTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var control = (OverflowFadingTextBlock)dependencyObject;
        control._textBlock.Text = (string?)eventArgs.NewValue ?? string.Empty;
    }

    private static Brush CreateFadeMask()
    {
        var mask = new LinearGradientBrush(
            Colors.Black,
            Colors.Transparent,
            new Point(0, 0),
            new Point(1, 0));
        mask.GradientStops[0].Offset = 0.7;
        mask.Freeze();
        return mask;
    }
}
