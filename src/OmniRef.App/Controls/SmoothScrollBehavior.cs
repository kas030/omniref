using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OmniRef.App.Controls;

public static class SmoothScrollBehavior
{
    private static readonly ConditionalWeakTable<ScrollViewer, AnimationState> States = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool ScrollBy(ScrollViewer scrollViewer, double horizontalDelta, double verticalDelta)
    {
        ArgumentNullException.ThrowIfNull(scrollViewer);

        if (!SystemParameters.ClientAreaAnimation)
        {
            var horizontalTarget = Math.Clamp(
                scrollViewer.HorizontalOffset + horizontalDelta,
                0,
                scrollViewer.ScrollableWidth);
            var verticalTarget = Math.Clamp(
                scrollViewer.VerticalOffset + verticalDelta,
                0,
                scrollViewer.ScrollableHeight);
            var changed = Math.Abs(horizontalTarget - scrollViewer.HorizontalOffset) >= 0.01 ||
                          Math.Abs(verticalTarget - scrollViewer.VerticalOffset) >= 0.01;
            scrollViewer.ScrollToHorizontalOffset(horizontalTarget);
            scrollViewer.ScrollToVerticalOffset(verticalTarget);
            return changed;
        }

        return States.GetValue(scrollViewer, static viewer => new AnimationState(viewer))
            .ScrollBy(horizontalDelta, verticalDelta);
    }

    public static void Stop(ScrollViewer scrollViewer)
    {
        ArgumentNullException.ThrowIfNull(scrollViewer);
        if (States.TryGetValue(scrollViewer, out var state))
        {
            state.Stop();
        }
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not ScrollViewer scrollViewer)
        {
            throw new InvalidOperationException(
                $"{nameof(SmoothScrollBehavior)} can only be attached to a ScrollViewer.");
        }

        if ((bool)eventArgs.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            if (States.TryGetValue(scrollViewer, out var state))
            {
                state.Stop();
            }
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (sender is not ScrollViewer scrollViewer || eventArgs.Delta == 0)
        {
            return;
        }

        var horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
                         (scrollViewer.ScrollableHeight <= 0 && scrollViewer.ScrollableWidth > 0);
        var delta = -eventArgs.Delta;
        eventArgs.Handled = ScrollBy(
            scrollViewer,
            horizontal ? delta : 0,
            horizontal ? 0 : delta);
    }

    private sealed class AnimationState
    {
        private const double AngularFrequency = 36;
        private const double PositionEpsilon = 0.25;
        private const double VelocityEpsilon = 6;
        private const double InitialFrameSeconds = 1d / 120;
        private const double MaximumFrameSeconds = 1d / 20;

        private readonly ScrollViewer _scrollViewer;
        private double _horizontalOffset;
        private double _verticalOffset;
        private double _targetHorizontalOffset;
        private double _targetVerticalOffset;
        private double _horizontalVelocity;
        private double _verticalVelocity;
        private TimeSpan? _lastRenderingTime;
        private bool _isActive;

        public AnimationState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _scrollViewer.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            _scrollViewer.PreviewKeyDown += OnPreviewKeyDown;
            _scrollViewer.Unloaded += OnUnloaded;
        }

        public bool ScrollBy(double horizontalDelta, double verticalDelta)
        {
            if (!_isActive)
            {
                _horizontalOffset = _scrollViewer.HorizontalOffset;
                _verticalOffset = _scrollViewer.VerticalOffset;
                _targetHorizontalOffset = _horizontalOffset;
                _targetVerticalOffset = _verticalOffset;
                _horizontalVelocity = 0;
                _verticalVelocity = 0;
            }

            var horizontalTarget = Math.Clamp(
                _targetHorizontalOffset + horizontalDelta,
                0,
                _scrollViewer.ScrollableWidth);
            var verticalTarget = Math.Clamp(
                _targetVerticalOffset + verticalDelta,
                0,
                _scrollViewer.ScrollableHeight);
            if (Math.Abs(horizontalTarget - _targetHorizontalOffset) < 0.01 &&
                Math.Abs(verticalTarget - _targetVerticalOffset) < 0.01)
            {
                return false;
            }

            _targetHorizontalOffset = horizontalTarget;
            _targetVerticalOffset = verticalTarget;
            if (!_isActive)
            {
                _isActive = true;
                _lastRenderingTime = null;
                CompositionTarget.Rendering += OnRendering;
            }

            return true;
        }

        public void Stop()
        {
            if (_isActive)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isActive = false;
            }

            _lastRenderingTime = null;
            _horizontalOffset = _scrollViewer.HorizontalOffset;
            _verticalOffset = _scrollViewer.VerticalOffset;
            _targetHorizontalOffset = _scrollViewer.HorizontalOffset;
            _targetVerticalOffset = _scrollViewer.VerticalOffset;
            _horizontalVelocity = 0;
            _verticalVelocity = 0;
        }

        private void OnRendering(object? sender, EventArgs eventArgs)
        {
            if (eventArgs is not RenderingEventArgs renderingEventArgs)
            {
                return;
            }

            var renderingTime = renderingEventArgs.RenderingTime;
            var elapsedSeconds = _lastRenderingTime is { } lastRenderingTime
                ? (renderingTime - lastRenderingTime).TotalSeconds
                : InitialFrameSeconds;
            _lastRenderingTime = renderingTime;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            elapsedSeconds = Math.Min(elapsedSeconds, MaximumFrameSeconds);
            _targetHorizontalOffset = Math.Min(
                _targetHorizontalOffset,
                _scrollViewer.ScrollableWidth);
            _targetVerticalOffset = Math.Min(
                _targetVerticalOffset,
                _scrollViewer.ScrollableHeight);
            AdvanceAxis(
                ref _horizontalOffset,
                ref _horizontalVelocity,
                _targetHorizontalOffset,
                elapsedSeconds,
                _scrollViewer.ScrollableWidth);
            AdvanceAxis(
                ref _verticalOffset,
                ref _verticalVelocity,
                _targetVerticalOffset,
                elapsedSeconds,
                _scrollViewer.ScrollableHeight);

            _scrollViewer.ScrollToHorizontalOffset(_horizontalOffset);
            _scrollViewer.ScrollToVerticalOffset(_verticalOffset);

            if (IsSettled(
                    _horizontalOffset,
                    _horizontalVelocity,
                    _targetHorizontalOffset) &&
                IsSettled(
                    _verticalOffset,
                    _verticalVelocity,
                    _targetVerticalOffset))
            {
                _scrollViewer.ScrollToHorizontalOffset(_targetHorizontalOffset);
                _scrollViewer.ScrollToVerticalOffset(_targetVerticalOffset);
                Stop();
            }
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs) => Stop();

        private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs) => Stop();

        private void OnUnloaded(object sender, RoutedEventArgs eventArgs) => Stop();

        private static void AdvanceAxis(
            ref double offset,
            ref double velocity,
            double target,
            double elapsedSeconds,
            double maximumOffset)
        {
            var displacement = offset - target;
            var coefficient = velocity + (AngularFrequency * displacement);
            var decay = Math.Exp(-AngularFrequency * elapsedSeconds);
            var nextDisplacement = (displacement + (coefficient * elapsedSeconds)) * decay;
            var nextVelocity =
                (coefficient - (AngularFrequency * (displacement + (coefficient * elapsedSeconds)))) * decay;

            offset = Math.Clamp(target + nextDisplacement, 0, maximumOffset);
            velocity = nextVelocity;
            if ((offset <= 0 && velocity < 0) || (offset >= maximumOffset && velocity > 0))
            {
                velocity = 0;
            }
        }

        private static bool IsSettled(double offset, double velocity, double target) =>
            Math.Abs(target - offset) <= PositionEpsilon &&
            Math.Abs(velocity) <= VelocityEpsilon;
    }
}
