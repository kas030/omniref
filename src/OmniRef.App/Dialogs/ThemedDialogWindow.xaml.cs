using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OmniRef.Infrastructure.Windows.Shell;

namespace OmniRef.App.Dialogs;

internal sealed partial class ThemedDialogWindow : Window
{
    private readonly DialogButtons _buttons;
    private Button? _defaultButton;
    private IntPtr _windowHandle;

    internal ThemedDialogWindow(
        string title,
        string message,
        DialogButtons buttons,
        DialogIcon icon,
        string okText,
        string yesText,
        string noText,
        string closeText)
    {
        InitializeComponent();
        _buttons = buttons;
        Title = title;
        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        OkButton.Content = okText;
        YesButton.Content = yesText;
        NoButton.Content = noText;
        CloseButton.ToolTip = closeText;
        AutomationProperties.SetName(CloseButton, closeText);

        ConfigureButtons();
        ConfigureIcon(icon);
        Loaded += OnLoaded;
    }

    internal DialogChoice Result { get; private set; }

    private void ConfigureButtons()
    {
        if (_buttons == DialogButtons.Ok)
        {
            OkButton.Visibility = Visibility.Visible;
            _defaultButton = OkButton;
            _defaultButton.IsDefault = true;
            return;
        }

        NoButton.Visibility = Visibility.Visible;
        YesButton.Visibility = Visibility.Visible;
        _defaultButton = YesButton;
        _defaultButton.IsDefault = true;
    }

    private void ConfigureIcon(DialogIcon icon)
    {
        InformationIcon.Visibility = icon == DialogIcon.Information
            ? Visibility.Visible
            : Visibility.Collapsed;
        WarningIcon.Visibility = icon == DialogIcon.Warning
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorIcon.Visibility = icon == DialogIcon.Error
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        ApplyNativeWindowFrame();
        _defaultButton?.Focus();
    }

    private void DialogWindow_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        WindowsWindowAnimation.RefreshSystemWindowFrame(_windowHandle);
        ApplyNativeWindowFrame();
    }

    private void DialogWindow_SizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        ApplyNativeWindowFrame();

    private void DialogWindow_DpiChanged(object sender, DpiChangedEventArgs eventArgs) =>
        ApplyNativeWindowFrame();

    private void ApplyNativeWindowFrame()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var cornerRadius = Math.Max(1, (int)Math.Round(12 * dpiScale));
        WindowsWindowAnimation.ApplyRoundedWindowRegion(_windowHandle, cornerRadius);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs) => Close();

    private void DialogButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        Result = sender == YesButton
            ? DialogChoice.Yes
            : sender == NoButton
                ? DialogChoice.No
                : DialogChoice.Ok;
        Close();
    }

    private void DialogWindow_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        if (_buttons == DialogButtons.YesNo)
        {
            Result = DialogChoice.No;
        }
        else
        {
            Result = DialogChoice.Ok;
        }

        Close();
        eventArgs.Handled = true;
    }
}
