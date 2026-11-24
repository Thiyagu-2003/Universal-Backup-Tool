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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(StateBadgeText))]
    [NotifyPropertyChangedFor(nameof(StateBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(StateBadgeSoftBrush))]
    [NotifyPropertyChangedFor(nameof(TileBrush))]
    [NotifyPropertyChangedFor(nameof(TileSoftBrush))]
    [NotifyPropertyChangedFor(nameof(TileIconGeometry))]
    private string _state = "Unknown";

    public string Path { get; set; } = "";
    public string[] Bindings { get; set; } = [];
    public string Pool { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int AppCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBadgeText))]
    [NotifyPropertyChangedFor(nameof(StateBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(StateBadgeSoftBrush))]
    [NotifyPropertyChangedFor(nameof(TileBrush))]
    [NotifyPropertyChangedFor(nameof(TileSoftBrush))]
    [NotifyPropertyChangedFor(nameof(TileIconGeometry))]
    private bool _available;

    [ObservableProperty]
    private string? _reason;

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

    public Brush StateBadgeBrush => (Brush)(Application.Current?.TryFindResource(
        !Available ? "BrushCoral" : (IsRunning ? "BrushJade" : "BrushSaffron")) ?? Brushes.Gray);

    public Brush StateBadgeSoftBrush => (Brush)(Application.Current?.TryFindResource(
        !Available ? "BrushCoralSoft" : (IsRunning ? "BrushJadeSoft" : "BrushSaffronSoft")) ?? Brushes.LightGray);

    public Brush TileBrush => StateBadgeBrush;
    public Brush TileSoftBrush => StateBadgeSoftBrush;

    public Geometry TileIconGeometry => (Geometry)(Application.Current?.TryFindResource(
        !Available ? "IconClose" : (IsRunning ? "IconGlobe" : "IconPauseCircle")) ?? Geometry.Empty);

    public string SizeDisplay => IsSizeComputing
        ? "Scanning..."
        : (SizeBytes > 0 ? FormattingUtils.FormatBytes(SizeBytes) : "0 B");

    public string FilesDisplay => IsSizeComputing
        ? "counting files"
        : $"{FileCount:N0} files";

    public string PrimaryBinding => Bindings.Length > 0 ? Bindings[0] : "http://*:80";

    public bool HasHttps => Bindings.Any(b => b.StartsWith("https", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> Ports => Bindings.Select(b =>
    {
        var idx = b.LastIndexOf(':');
        return idx >= 0 ? b[(idx + 1)..].TrimEnd('/') : "";
    }).Where(p => !string.IsNullOrEmpty(p));

    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        if (Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (Pool.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (Bindings.Any(b => b.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
        if (Ports.Any(p => p.Equals(q, StringComparison.OrdinalIgnoreCase) || p.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(StateBadgeText));
        OnPropertyChanged(nameof(StateBadgeBrush));
        OnPropertyChanged(nameof(StateBadgeSoftBrush));
        OnPropertyChanged(nameof(TileBrush));
        OnPropertyChanged(nameof(TileSoftBrush));
        OnPropertyChanged(nameof(TileIconGeometry));
    }

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
