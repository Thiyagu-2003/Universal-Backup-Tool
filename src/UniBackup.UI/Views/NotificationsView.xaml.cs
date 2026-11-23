using System.Windows;
using System.Windows.Controls;

namespace UniBackup.UI.Views;

public partial class NotificationsView : UserControl
{
    public NotificationsView()
    {
        InitializeComponent();
    }

    private void ChannelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        if (EmailChannelPanel != null) EmailChannelPanel.Visibility = tag == "Email" ? Visibility.Visible : Visibility.Collapsed;
        if (SlackChannelPanel != null) SlackChannelPanel.Visibility = tag == "Slack" ? Visibility.Visible : Visibility.Collapsed;
        if (TeamsChannelPanel != null) TeamsChannelPanel.Visibility = tag == "Teams" ? Visibility.Visible : Visibility.Collapsed;
        if (DiscordChannelPanel != null) DiscordChannelPanel.Visibility = tag == "Discord" ? Visibility.Visible : Visibility.Collapsed;
        if (TelegramChannelPanel != null) TelegramChannelPanel.Visibility = tag == "Telegram" ? Visibility.Visible : Visibility.Collapsed;
        if (PushoverChannelPanel != null) PushoverChannelPanel.Visibility = tag == "Pushover" ? Visibility.Visible : Visibility.Collapsed;
        if (NtfyChannelPanel != null) NtfyChannelPanel.Visibility = tag == "Ntfy" ? Visibility.Visible : Visibility.Collapsed;
        if (PagerDutyChannelPanel != null) PagerDutyChannelPanel.Visibility = tag == "PagerDuty" ? Visibility.Visible : Visibility.Collapsed;
        if (CustomWebhookChannelPanel != null) CustomWebhookChannelPanel.Visibility = tag == "CustomWebhook" ? Visibility.Visible : Visibility.Collapsed;
        if (HeartbeatChannelPanel != null) HeartbeatChannelPanel.Visibility = tag == "Heartbeat" ? Visibility.Visible : Visibility.Collapsed;
    }
}
