using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OmniRef.App.Behaviors;

public static class ToolTipPlacementBehavior
{
    private const double WindowMargin = 8;
    private const double TargetGap = 6;

    public static readonly DependencyProperty KeepInsideWindowProperty = DependencyProperty.RegisterAttached(
        "KeepInsideWindow",
        typeof(bool),
        typeof(ToolTipPlacementBehavior),
        new PropertyMetadata(false, OnKeepInsideWindowChanged));

    public static void SetKeepInsideWindow(DependencyObject element, bool value) =>
        element.SetValue(KeepInsideWindowProperty, value);

    public static bool GetKeepInsideWindow(DependencyObject element) =>
        (bool)element.GetValue(KeepInsideWindowProperty);

    private static void OnKeepInsideWindowChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not ToolTip toolTip || eventArgs.NewValue is not true)
        {
            return;
        }

        toolTip.Placement = PlacementMode.Custom;
        toolTip.HorizontalOffset = 0;
        toolTip.VerticalOffset = 0;
        toolTip.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            CalculatePlacement(toolTip, popupSize, targetSize);
    }

    private static CustomPopupPlacement[] CalculatePlacement(
        ToolTip toolTip,
        Size popupSize,
        Size targetSize)
    {
        if (toolTip.PlacementTarget is not FrameworkElement target ||
            Window.GetWindow(target) is not { } window)
        {
            return [CenteredBelow(popupSize, targetSize)];
        }

        Point targetOrigin;
        try
        {
            targetOrigin = target.TranslatePoint(new Point(), window);
        }
        catch (InvalidOperationException)
        {
            return [CenteredBelow(popupSize, targetSize)];
        }

        var windowWidth = Math.Max(0, window.ActualWidth);
        var windowHeight = Math.Max(0, window.ActualHeight);
        var maximumLeft = Math.Max(WindowMargin, windowWidth - popupSize.Width - WindowMargin);
        var left = Math.Clamp(
            targetOrigin.X + ((targetSize.Width - popupSize.Width) / 2),
            WindowMargin,
            maximumLeft);

        var below = targetOrigin.Y + targetSize.Height + TargetGap;
        var above = targetOrigin.Y - popupSize.Height - TargetGap;
        var maximumTop = Math.Max(WindowMargin, windowHeight - popupSize.Height - WindowMargin);
        var top = below + popupSize.Height <= windowHeight - WindowMargin
            ? below
            : above >= WindowMargin
                ? above
                : Math.Clamp(below, WindowMargin, maximumTop);

        return
        [
            new CustomPopupPlacement(
                new Point(left - targetOrigin.X, top - targetOrigin.Y),
                PopupPrimaryAxis.None)
        ];
    }

    private static CustomPopupPlacement CenteredBelow(Size popupSize, Size targetSize) =>
        new(
            new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + TargetGap),
            PopupPrimaryAxis.None);
}
