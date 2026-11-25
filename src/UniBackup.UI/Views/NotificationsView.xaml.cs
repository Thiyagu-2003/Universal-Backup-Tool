using System.Windows;
using System.Windows.Controls;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Views;

public partial class NotificationsView : UserControl
{
    public NotificationsView()
    {
        InitializeComponent();
    }

    private void TextBox_Dirty(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && DataContext is MainViewModel vm)
        {
            vm.MarkNotificationsDirty();
        }
    }

    private void Input_Dirty(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && DataContext is MainViewModel vm)
        {
            vm.MarkNotificationsDirty();
        }
    }
}
