using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UniBackup.UI.ViewModels;

namespace UniBackup.UI.Dialogs;

public partial class ExecutionDetailsDialog : Window
{
    private readonly Action<string>? _onOpenLogs;
    public ActivityRowVm Row { get; }

    public ExecutionDetailsDialog(ActivityRowVm row, Action<string>? onOpenLogs = null)
    {
        InitializeComponent();
        Row = row;
        DataContext = row;
        _onOpenLogs = onOpenLogs;

        Loaded += ExecutionDetailsDialog_Loaded;
        Closed += ExecutionDetailsDialog_Closed;
    }

    private MainWindow? GetMainWindow()
    {
        return Owner as MainWindow ?? Application.Current?.MainWindow as MainWindow;
    }

    private void ExecutionDetailsDialog_Loaded(object sender, RoutedEventArgs e)
    {
        GetMainWindow()?.ShowModalBlur();

        if (SystemParameters.ClientAreaAnimation && DialogCard != null)
        {
            var scaleAnim = new DoubleAnimation(0.96, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            var fadeAnim = new DoubleAnimation(0.6, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            DialogCard.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }
    }

    private void ExecutionDetailsDialog_Closed(object? sender, EventArgs e)
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
        Close();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onOpenLogs?.Invoke(Row.JobName);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
