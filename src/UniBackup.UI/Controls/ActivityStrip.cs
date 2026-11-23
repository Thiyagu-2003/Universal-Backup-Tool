using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UniBackup.UI.Controls;

public class ActivityStrip : Control
{
    static ActivityStrip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ActivityStrip), new FrameworkPropertyMetadata(typeof(ActivityStrip)));
    }

    public static readonly DependencyProperty RunsProperty =
        DependencyProperty.Register(nameof(Runs), typeof(IEnumerable), typeof(ActivityStrip),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsJobRunningProperty =
        DependencyProperty.Register(nameof(IsJobRunning), typeof(bool), typeof(ActivityStrip),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Runs
    {
        get => (IEnumerable?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    public bool IsJobRunning
    {
        get => (bool)GetValue(IsJobRunningProperty);
        set => SetValue(IsJobRunningProperty, value);
    }

    public ActivityStrip()
    {
        Height = 34;
        SnapsToDevicePixels = true;
        ToolTip = "Recent 24-hour backup activity (24 h ago → Now)";
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        // Background track (radius 6)
        var sunkenBrush = TryFindResource("BrushSunken") as Brush ?? new SolidColorBrush(Color.FromRgb(0xEA, 0xEF, 0xF6));
        var lineBrush = TryFindResource("BrushLine") as Brush ?? new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xF0));
        var penLine = new Pen(lineBrush, 0.75);
        penLine.Freeze();

        dc.DrawRoundedRectangle(sunkenBrush, penLine, new Rect(0, 0, w, h), 6, 6);

        // 6 vertical 4-hour interval gridlines
        var gridPen = new Pen(lineBrush, 1.0) { DashStyle = DashStyles.Dot };
        gridPen.Freeze();

        for (int i = 1; i < 6; i++)
        {
            var x = (w / 6.0) * i;
            dc.DrawLine(gridPen, new Point(x, 4), new Point(x, h - 4));
        }

        // Palette references
        var cobalt = TryFindResource("BrushCobalt") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2D, 0x5B, 0xFF));
        var sky = TryFindResource("BrushSky") as Brush ?? new SolidColorBrush(Color.FromRgb(0x0A, 0x7F, 0xD4));
        var inkFaint = TryFindResource("BrushInkFaint") as Brush ?? new SolidColorBrush(Color.FromRgb(0x80, 0x8F, 0xA5));
        var saffron = TryFindResource("BrushSaffron") as Brush ?? new SolidColorBrush(Color.FromRgb(0xC9, 0x7A, 0x00));
        var coral = TryFindResource("BrushCoral") as Brush ?? new SolidColorBrush(Color.FromRgb(0xE0, 0x29, 0x3F));

        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-24);

        if (Runs != null)
        {
            foreach (var item in Runs)
            {
                DateTime time = DateTime.MinValue;
                string type = "FULL";
                string status = "SUCCESS";

                // Extract properties via reflection or dynamic to support diverse models/viewmodels
                var tProp = item.GetType().GetProperty("Timestamp") ?? item.GetType().GetProperty("StartTime") ?? item.GetType().GetProperty("CreatedUtc");
                if (tProp?.GetValue(item) is DateTime dt) time = dt.ToUniversalTime();

                var typeProp = item.GetType().GetProperty("BackupType") ?? item.GetType().GetProperty("Type") ?? item.GetType().GetProperty("Trigger");
                var rawVal = typeProp?.GetValue(item);
                if (rawVal is string tp) type = tp.ToUpperInvariant();
                else if (rawVal != null) type = rawVal.ToString()!.ToUpperInvariant();

                if (item is UniBackup.Core.Models.RunHistoryRecord rhr && rhr.SqlDatabases.Count > 0)
                {
                    type = rhr.SqlDatabases[0].BackupType.ToString().ToUpperInvariant();
                }

                var statusProp = item.GetType().GetProperty("Status") ?? item.GetType().GetProperty("Result");
                if (statusProp?.GetValue(item) is string st) status = st.ToUpperInvariant();

                if (time < windowStart || time > now) continue;

                var ratio = (time - windowStart).TotalHours / 24.0;
                var x = Math.Clamp(ratio * (w - 12) + 6, 4, w - 8);

                // Tick height: Full = 25px, Diff = 17px, Log = 9px
                double tickH = 25;
                if (type.Contains("DIFF")) tickH = 17;
                else if (type.Contains("LOG") || type.Contains("TRN")) tickH = 9;

                // Color
                Brush tickBrush = cobalt;
                if (status.Contains("FAIL") || status.Contains("ERROR")) tickBrush = coral;
                else if (status.Contains("PARTIAL") || status.Contains("WARN")) tickBrush = saffron;
                else if (type.Contains("DIFF")) tickBrush = sky;
                else if (type.Contains("LOG") || type.Contains("TRN")) tickBrush = inkFaint;

                var y = (h - tickH) / 2.0;
                dc.DrawRoundedRectangle(tickBrush, null, new Rect(x - 1.5, y, 3.0, tickH), 1.5, 1.5);
            }
        }

        // Running job indicator (pulsing ghost tick at right edge)
        if (IsJobRunning)
        {
            var ghostBrush = cobalt.Clone();
            ghostBrush.Opacity = 0.55;
            dc.DrawRoundedRectangle(ghostBrush, null, new Rect(w - 7, (h - 25) / 2.0, 3.5, 25), 1.75, 1.75);
        }
    }
}
