using System.Windows;
using OmniRef.App.Services;

namespace OmniRef.App.Dialogs;

internal enum DialogButtons
{
    Ok,
    YesNo
}

internal enum DialogIcon
{
    Information,
    Warning,
    Error
}

internal enum DialogChoice
{
    None,
    Ok,
    Yes,
    No
}

internal static class DialogService
{
    public static DialogChoice Show(
        Window? owner,
        LocalizationService localization,
        string title,
        string message,
        DialogButtons buttons,
        DialogIcon icon)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => Show(owner, localization, title, message, buttons, icon));
        }

        var dialog = new ThemedDialogWindow(
            title,
            message,
            buttons,
            icon,
            localization["DialogOk"],
            localization["DialogYes"],
            localization["DialogNo"],
            localization["CloseWindow"]);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}
