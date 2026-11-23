using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UniBackup.UI.Controls;

public partial class StatusDot : UserControl
{
    public static readonly DependencyProperty DotBrushProperty =
        DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(StatusDot),
            new PropertyMetadata(null));

    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    public StatusDot()
    {
        InitializeComponent();
        Loaded += StatusDot_Loaded;
    }

    private void StatusDot_Loaded(object sender, RoutedEventArgs e)
    {
        if (DotBrush == null)
        {
            DotBrush = TryFindResource("BrushJade") as Brush ?? new SolidColorBrush(Color.FromRgb(0x00, 0x94, 0x6A));
        }

        if (SystemParameters.ClientAreaAnimation)
        {
            StartPulseAnimation();
        }
    }

    private void StartPulseAnimation()
    {
        var scaleAnim = new DoubleAnimation(1.0, 2.2, new Duration(TimeSpan.FromSeconds(2.4)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var opacityAnim = new DoubleAnimation(0.9, 0.0, new Duration(TimeSpan.FromSeconds(2.4)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        PulseRing.BeginAnimation(OpacityProperty, opacityAnim);
    }
}
