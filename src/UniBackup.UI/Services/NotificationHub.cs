using System;

namespace UniBackup.UI.Services;

public enum ToastType
{
    Success,
    Info,
    Warning,
    Error
}

public class ToastNotification
{
    public string Title { get; set; } = "Notification";
    public string Message { get; set; } = "";
    public ToastType Type { get; set; } = ToastType.Success;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(4.5);
    public string? ActionLabel { get; set; }
    public Action? Action { get; set; }
    public bool HasAction => !string.IsNullOrEmpty(ActionLabel) && Action != null;
}

public static class NotificationHub
{
    public static event EventHandler<ToastNotification>? ToastRequested;

    public static void ShowToast(string message, string title = "Success", ToastType type = ToastType.Success, double durationSeconds = 4.5, string? actionLabel = null, Action? action = null)
    {
        ToastRequested?.Invoke(null, new ToastNotification
        {
            Title = title,
            Message = message,
            Type = type,
            Duration = TimeSpan.FromSeconds(action != null ? Math.Max(durationSeconds, 7.0) : durationSeconds),
            ActionLabel = actionLabel,
            Action = action
        });
    }

    public static void ShowSuccess(string message, string title = "Success", string? actionLabel = null, Action? action = null) =>
        ShowToast(message, title, ToastType.Success, 4.5, actionLabel, action);

    public static void ShowInfo(string message, string title = "Information") =>
        ShowToast(message, title, ToastType.Info, 4.5);

    public static void ShowWarning(string message, string title = "Warning") =>
        ShowToast(message, title, ToastType.Warning, 5.0);

    public static void ShowError(string message, string title = "Error") =>
        ShowToast(message, title, ToastType.Error, 6.0);
}
