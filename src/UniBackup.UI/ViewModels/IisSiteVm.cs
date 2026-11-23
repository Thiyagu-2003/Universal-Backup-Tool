using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UniBackup.Core.Common;

namespace UniBackup.UI.ViewModels;

public partial class IisSiteVm : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string State { get; set; } = "Unknown";
    public string Path { get; set; } = "";
    public string[] Bindings { get; set; } = [];
    public string Pool { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int AppCount { get; set; }
    public bool Available { get; set; }
    public string? Reason { get; set; }

    [ObservableProperty]
    private long _sizeBytes;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private bool _isSizeComputing = true;

    [ObservableProperty]
    private double _sizeRatio = 0.0;

    public bool IsRunning => string.Equals(State, "Started", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsStopped => string.Equals(State, "Stopped", StringComparison.OrdinalIgnoreCase);

    public string StateBadgeText => !Available
        ? "Unavailable"
        : (IsRunning ? "Running" : (IsStopped ? "Stopped" : "Unknown"));

    public Brush StateBadgeBrush => (Brush)(Application.Current.TryFindResource(
        !Available ? "BrushCoral" : (IsRunning ? "BrushJade" : "BrushSaffron")) ?? Brushes.Gray);

    public Brush StateBadgeSoftBrush => (Brush)(Application.Current.TryFindResource(
        !Available ? "BrushCoralSoft" : (IsRunning ? "BrushJadeSoft" : "BrushSaffronSoft")) ?? Brushes.LightGray);

    public Brush TileBrush => StateBadgeBrush;
    public Brush TileSoftBrush => StateBadgeSoftBrush;

    public Geometry TileIconGeometry => (Geometry)(Application.Current.TryFindResource(
        !Available ? "IconClose" : (IsRunning ? "IconGlobe" : "IconPauseCircle")) ?? Geometry.Empty);

    public string SizeDisplay => IsSizeComputing
        ? "Scanning..."
        : (SizeBytes > 0 ? FormattingUtils.FormatBytes(SizeBytes) : "0 B");

    public string FilesDisplay => IsSizeComputing
        ? "counting files"
        : $"{FileCount:N0} files";

    public string PrimaryBinding => Bindings.Length > 0 ? Bindings[0] : "http://*:80";

    public bool HasHttps => Bindings.Any(b => b.StartsWith("https", StringComparison.OrdinalIgnoreCase));

    public void NotifySizeChanged()
    {
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(IsSizeComputing));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(FilesDisplay));
        OnPropertyChanged(nameof(SizeRatio));
    }
}
