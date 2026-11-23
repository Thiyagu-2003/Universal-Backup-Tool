using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UniBackup.UI.Controls;

public partial class KpiCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(KpiCard), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(KpiCard), new PropertyMetadata("0"));

    public static readonly DependencyProperty NumericValueProperty =
        DependencyProperty.Register(nameof(NumericValue), typeof(double), typeof(KpiCard),
            new PropertyMetadata(0.0, OnNumericValueChanged));

    public static readonly DependencyProperty SubTextProperty =
        DependencyProperty.Register(nameof(SubText), typeof(string), typeof(KpiCard), new PropertyMetadata(""));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(KpiCard), new PropertyMetadata(null));

    public static readonly DependencyProperty TileBrushProperty =
        DependencyProperty.Register(nameof(TileBrush), typeof(Brush), typeof(KpiCard), new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double NumericValue
    {
        get => (double)GetValue(NumericValueProperty);
        set => SetValue(NumericValueProperty, value);
    }

    public string SubText
    {
        get => (string)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    public Geometry Icon
    {
        get => (Geometry)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Brush TileBrush
    {
        get => (Brush)GetValue(TileBrushProperty);
        set => SetValue(TileBrushProperty, value);
    }

    public KpiCard()
    {
        InitializeComponent();
        GenerateDefaultSparkline();
    }

    private static void OnNumericValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KpiCard card && e.NewValue is double target)
        {
            card.AnimateCountUp(target);
        }
    }

    private void AnimateCountUp(double target)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Value = target.ToString("N0");
            return;
        }

        var anim = new DoubleAnimation(0, target, new Duration(TimeSpan.FromMilliseconds(750)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var dummy = new FrameworkElement();
        anim.CurrentTimeInvalidated += (s, e) =>
        {
            if (s is AnimationClock clock && clock.CurrentProgress.HasValue)
            {
                var currentVal = target * clock.CurrentProgress.Value;
                Dispatcher.InvokeAsync(() =>
                {
                    Value = ((int)currentVal).ToString();
                });
            }
        };
        dummy.BeginAnimation(WidthProperty, anim);
    }

    private void GenerateDefaultSparkline()
    {
        var points = new PointCollection
        {
            new Point(0, 40),
            new Point(20, 32),
            new Point(40, 36),
            new Point(60, 20),
            new Point(80, 26),
            new Point(100, 14),
            new Point(120, 18),
            new Point(140, 6)
        };
        SparklinePolyline.Points = points;
    }
}
