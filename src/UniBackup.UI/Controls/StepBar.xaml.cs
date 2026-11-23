using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UniBackup.UI.Controls;

public partial class StepBar : UserControl
{
    public static readonly DependencyProperty CurrentStepProperty =
        DependencyProperty.Register(nameof(CurrentStep), typeof(int), typeof(StepBar),
            new PropertyMetadata(1, OnCurrentStepChanged));

    public static readonly DependencyProperty StepSelectedCommandProperty =
        DependencyProperty.Register(nameof(StepSelectedCommand), typeof(ICommand), typeof(StepBar),
            new PropertyMetadata(null));

    public int CurrentStep
    {
        get => (int)GetValue(CurrentStepProperty);
        set => SetValue(CurrentStepProperty, value);
    }

    public ICommand? StepSelectedCommand
    {
        get => (ICommand?)GetValue(StepSelectedCommandProperty);
        set => SetValue(StepSelectedCommandProperty, value);
    }

    public StepBar()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateStepVisuals();
    }

    private static void OnCurrentStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StepBar bar)
        {
            bar.UpdateStepVisuals();
        }
    }

    public void UpdateStepVisuals()
    {
        UpdateCell(Step1Button, 1);
        UpdateCell(Step2Button, 2);
        UpdateCell(Step3Button, 3);
        UpdateCell(Step4Button, 4);
    }

    private void UpdateCell(Button btn, int stepNumber)
    {
        if (btn == null) return;
        btn.ApplyTemplate();

        var bd = btn.Template.FindName("bd", btn) as Border;
        var circle = btn.Template.FindName("circle", btn) as Border;
        var stepNum = btn.Template.FindName("stepNum", btn) as TextBlock;
        var checkIcon = btn.Template.FindName("checkIcon", btn) as System.Windows.Shapes.Path;
        var title = btn.Template.FindName("title", btn) as TextBlock;

        if (bd == null || circle == null || stepNum == null || checkIcon == null || title == null) return;

        var cobalt = TryFindResource("BrushCobalt") as Brush ?? Brushes.Blue;
        var cobaltSoft = TryFindResource("BrushCobaltSoft") as Brush ?? Brushes.LightBlue;
        var jade = TryFindResource("BrushJade") as Brush ?? Brushes.Green;
        var sunken = TryFindResource("BrushSunken") as Brush ?? Brushes.LightGray;
        var ink = TryFindResource("BrushInk") as Brush ?? Brushes.Black;
        var inkMuted = TryFindResource("BrushInkMuted") as Brush ?? Brushes.Gray;

        if (stepNumber == CurrentStep)
        {
            // Current step: cobalt-soft background, cobalt circle, cobalt title
            bd.Background = cobaltSoft;
            circle.Background = cobalt;
            stepNum.Visibility = Visibility.Visible;
            stepNum.Foreground = Brushes.White;
            checkIcon.Visibility = Visibility.Collapsed;
            title.Foreground = cobalt;
            btn.IsEnabled = true;
            btn.Cursor = Cursors.Hand;
        }
        else if (stepNumber < CurrentStep)
        {
            // Completed step: jade circle with check, clickable
            bd.Background = Brushes.Transparent;
            circle.Background = jade;
            stepNum.Visibility = Visibility.Collapsed;
            checkIcon.Visibility = Visibility.Visible;
            title.Foreground = ink;
            btn.IsEnabled = true;
            btn.Cursor = Cursors.Hand;
        }
        else
        {
            // Future step: muted, disabled
            bd.Background = Brushes.Transparent;
            circle.Background = sunken;
            stepNum.Visibility = Visibility.Visible;
            stepNum.Foreground = inkMuted;
            checkIcon.Visibility = Visibility.Collapsed;
            title.Foreground = inkMuted;
            btn.IsEnabled = false;
            btn.Cursor = Cursors.Arrow;
        }
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out var step))
        {
            if (step <= CurrentStep)
            {
                CurrentStep = step;
                StepSelectedCommand?.Execute(step.ToString());
            }
        }
    }
}
