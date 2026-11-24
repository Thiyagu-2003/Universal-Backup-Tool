using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Dialogs;

public partial class IisBulkBackupDialog : Window
{
    public IisBulkBackupViewModel ViewModel { get; }

    public IisBulkBackupDialog(IisBulkBackupViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.RequestClose = () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                DialogResult = true;
                Close();
            });
        };

        Loaded += IisBulkBackupDialog_Loaded;
        Closed += IisBulkBackupDialog_Closed;
    }

    private MainWindow? GetMainWindow()
    {
        return Owner as MainWindow ?? Application.Current?.MainWindow as MainWindow;
    }

    private void IisBulkBackupDialog_Loaded(object sender, RoutedEventArgs e)
    {
        GetMainWindow()?.ShowModalBlur();

        if (SystemParameters.ClientAreaAnimation && DialogCard != null)
        {
            var scaleAnim = new DoubleAnimation(0.96, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            DialogCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            DialogCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            var fadeAnim = new DoubleAnimation(0.6, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            DialogCard.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }
    }

    private void IisBulkBackupDialog_Closed(object? sender, EventArgs e)
    {
        GetMainWindow()?.HideModalBlur();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning) return;
        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !ViewModel.IsRunning)
        {
            DialogResult = true;
            Close();
        }
    }
}
