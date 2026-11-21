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
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(3.5);
}

public static class NotificationHub
{
    public static event EventHandler<ToastNotification>? ToastRequested;

    public static void ShowToast(string message, string title = "Success", ToastType type = ToastType.Success, double durationSeconds = 3.5)
    {
        ToastRequested?.Invoke(null, new ToastNotification
        {
            Title = title,
            Message = message,
            Type = type,
            Duration = TimeSpan.FromSeconds(durationSeconds)
        });
    }

    public static void ShowSuccess(string message, string title = "Success") =>
        ShowToast(message, title, ToastType.Success);

    public static void ShowInfo(string message, string title = "Information") =>
        ShowToast(message, title, ToastType.Info);

    public static void ShowWarning(string message, string title = "Warning") =>
        ShowToast(message, title, ToastType.Warning);

    public static void ShowError(string message, string title = "Error") =>
        ShowToast(message, title, ToastType.Error);
}
