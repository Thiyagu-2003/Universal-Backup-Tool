using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UniBackup.UI.Controls;

public class TimelineLanes : Control
{
    static TimelineLanes()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TimelineLanes), new FrameworkPropertyMetadata(typeof(TimelineLanes)));
    }

    public static readonly DependencyProperty SelectedTimeProperty =
        DependencyProperty.Register(nameof(SelectedTime), typeof(DateTime), typeof(TimelineLanes),
            new FrameworkPropertyMetadata(DateTime.UtcNow, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedTimeChanged));

    public static readonly DependencyProperty RangeHoursProperty =
        DependencyProperty.Register(nameof(RangeHours), typeof(double), typeof(TimelineLanes),
            new FrameworkPropertyMetadata(48.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackupsProperty =
        DependencyProperty.Register(nameof(Backups), typeof(IEnumerable), typeof(TimelineLanes),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly RoutedEvent SelectedTimeChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(SelectedTimeChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<DateTime>), typeof(TimelineLanes));

    public event RoutedPropertyChangedEventHandler<DateTime> SelectedTimeChanged
    {
        add => AddHandler(SelectedTimeChangedEvent, value);
        remove => RemoveHandler(SelectedTimeChangedEvent, value);
    }

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineLanes lanes && e.OldValue is DateTime oldVal && e.NewValue is DateTime newVal)
        {
            lanes.RaiseEvent(new RoutedPropertyChangedEventArgs<DateTime>(oldVal, newVal, SelectedTimeChangedEvent));
        }
    }

    public DateTime SelectedTime
    {
        get => (DateTime)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public double RangeHours
    {
        get => (double)GetValue(RangeHoursProperty);
        set => SetValue(RangeHoursProperty, value);
    }

    public IEnumerable? Backups
    {
        get => (IEnumerable?)GetValue(BackupsProperty);
        set => SetValue(BackupsProperty, value);
    }

    private bool _isDragging;

    public TimelineLanes()
    {
        Height = 120;
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Left)
        {
            Focus();
            _isDragging = true;
            CaptureMouse();
            UpdateTimeFromMouse(e.GetPosition(this).X);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging)
        {
            UpdateTimeFromMouse(e.GetPosition(this).X);
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var stepMinutes = shift ? 60 : 1;

        var now = DateTime.UtcNow;
        var start = now.AddHours(-RangeHours);

        switch (e.Key)
        {
            case Key.Left:
                SelectedTime = ClampTime(SelectedTime.AddMinutes(-stepMinutes), start, now);
                e.Handled = true;
                break;
            case Key.Right:
                SelectedTime = ClampTime(SelectedTime.AddMinutes(stepMinutes), start, now);
                e.Handled = true;
                break;
            case Key.Home:
                SelectedTime = start;
                e.Handled = true;
                break;
            case Key.End:
                SelectedTime = now;
                e.Handled = true;
                break;
        }
    }

    private static DateTime ClampTime(DateTime val, DateTime min, DateTime max)
    {
        if (val < min) return min;
        if (val > max) return max;
        return val;
    }

    private void UpdateTimeFromMouse(double mouseX)
    {
        var w = ActualWidth;
        if (w <= 20) return;

        var now = DateTime.UtcNow;
        var start = now.AddHours(-RangeHours);

        var ratio = Math.Clamp(mouseX / w, 0.0, 1.0);
        var totalSec = (now - start).TotalSeconds;
        var newTime = start.AddSeconds(ratio * totalSec);
        SelectedTime = newTime;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 20 || h <= 40) return;

        var panelBrush = TryFindResource("BrushPanel") as Brush ?? Brushes.White;
        var sunkenBrush = TryFindResource("BrushSunken") as Brush ?? new SolidColorBrush(Color.FromRgb(0xEA, 0xEF, 0xF6));
        var lineBrush = TryFindResource("BrushLine") as Brush ?? new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xF0));
        var lineStrong = TryFindResource("BrushLineStrong") as Brush ?? new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xE0));
        var inkMuted = TryFindResource("BrushInkMuted") as Brush ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x5A, 0x70));
        var inkFaint = TryFindResource("BrushInkFaint") as Brush ?? new SolidColorBrush(Color.FromRgb(0x80, 0x8F, 0xA5));
        var cobalt = TryFindResource("BrushCobalt") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2D, 0x5B, 0xFF));
        var sky = TryFindResource("BrushSky") as Brush ?? new SolidColorBrush(Color.FromRgb(0x0A, 0x7F, 0xD4));

        var penLine = new Pen(lineBrush, 1.0);
        penLine.Freeze();

        // Outer background
        dc.DrawRoundedRectangle(panelBrush, penLine, new Rect(0, 0, w, h), 10, 10);

        var laneH = 26.0;
        var laneTop1 = 12.0;
        var laneTop2 = laneTop1 + laneH + 4;
        var laneTop3 = laneTop2 + laneH + 4;

        // Draw 3 lanes background tracks
        var trackPen = new Pen(lineBrush, 0.5);
        trackPen.Freeze();
        dc.DrawRoundedRectangle(sunkenBrush, trackPen, new Rect(60, laneTop1, w - 70, laneH), 4, 4);
        dc.DrawRoundedRectangle(sunkenBrush, trackPen, new Rect(60, laneTop2, w - 70, laneH), 4, 4);
        dc.DrawRoundedRectangle(sunkenBrush, trackPen, new Rect(60, laneTop3, w - 70, laneH), 4, 4);

        // Lane labels
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        void DrawLabel(string text, double y)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, inkMuted, 1.0);
            dc.DrawText(ft, new Point(14, y + 6));
        }

        DrawLabel("FULL", laneTop1);
        DrawLabel("DIFF", laneTop2);
        DrawLabel("LOG", laneTop3);

        // 6-hour interval vertical gridlines (across 48h = 8 segments)
        var gridPen = new Pen(lineStrong, 0.75) { DashStyle = DashStyles.Dash };
        gridPen.Freeze();

        var trackWidth = w - 70;
        var trackLeft = 60.0;

        for (int i = 1; i <= 7; i++)
        {
            var gx = trackLeft + (trackWidth / 8.0) * i;
            dc.DrawLine(gridPen, new Point(gx, 8), new Point(gx, laneTop3 + laneH + 2));
        }

        var now = DateTime.UtcNow;
        var start = now.AddHours(-RangeHours);

        // Draw backup marks
        if (Backups != null)
        {
            foreach (var b in Backups)
            {
                DateTime dt = DateTime.MinValue;
                string type = "FULL";
                bool inChain = false;

                var tProp = b.GetType().GetProperty("Timestamp") ?? b.GetType().GetProperty("Time");
                if (tProp?.GetValue(b) is DateTime val) dt = val.ToUniversalTime();

                var tpProp = b.GetType().GetProperty("BackupType") ?? b.GetType().GetProperty("Type");
                if (tpProp?.GetValue(b) is string tp) type = tp.ToUpperInvariant();

                var chainProp = b.GetType().GetProperty("IsInRestoreChain") ?? b.GetType().GetProperty("InChain");
                if (chainProp?.GetValue(b) is bool c) inChain = c;

                if (dt < start || dt > now) continue;

                var ratio = (dt - start).TotalHours / RangeHours;
                var mx = trackLeft + ratio * trackWidth;

                if (type.Contains("FULL"))
                {
                    // 11x23 cobalt
                    var my = laneTop1 + (laneH - 23) / 2.0;
                    dc.DrawRoundedRectangle(cobalt, inChain ? new Pen(Brushes.White, 2.0) : null, new Rect(mx - 5.5, my, 11, 23), 2.5, 2.5);
                }
                else if (type.Contains("DIFF"))
                {
                    // 7x17 sky
                    var my = laneTop2 + (laneH - 17) / 2.0;
                    dc.DrawRoundedRectangle(sky, inChain ? new Pen(Brushes.White, 1.5) : null, new Rect(mx - 3.5, my, 7, 17), 2.0, 2.0);
                }
                else
                {
                    // 4x12 grey
                    var my = laneTop3 + (laneH - 12) / 2.0;
                    dc.DrawRoundedRectangle(inkFaint, inChain ? new Pen(Brushes.White, 1.0) : null, new Rect(mx - 2.0, my, 4, 12), 1.5, 1.5);
                }
            }
        }

        // Selected Time Thumb and Rule
        var selRatio = Math.Clamp((SelectedTime.ToUniversalTime() - start).TotalHours / RangeHours, 0.0, 1.0);
        var thumbX = trackLeft + selRatio * trackWidth;

        // Shaded region after the selected thumb (future is shaded)
        if (thumbX < trackLeft + trackWidth)
        {
            var shadeBrush = new SolidColorBrush(Color.FromArgb(50, 11, 20, 32));
            shadeBrush.Freeze();
            dc.DrawRectangle(shadeBrush, null, new Rect(thumbX, 8, (trackLeft + trackWidth) - thumbX, laneTop3 + laneH));
        }

        // Draggable vertical rule
        var rulePen = new Pen(cobalt, 2.0);
        rulePen.Freeze();
        dc.DrawLine(rulePen, new Point(thumbX, 4), new Point(thumbX, h - 6));

        // Cobalt top and bottom thumb caps
        dc.DrawRoundedRectangle(cobalt, null, new Rect(thumbX - 6, 2, 12, 10), 3, 3);
        dc.DrawRoundedRectangle(cobalt, null, new Rect(thumbX - 6, h - 12, 12, 10), 3, 3);
    }
}
