using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UniBackup.UI.Controls;

/// <summary>
/// Professional vector icon control supporting Microsoft Fluent System Icons
/// and authentic Simple Icons brand logos with theme-aware foreground brushes
/// and official brand multi-color renderings.
/// </summary>
public class AppIcon : Control
{
    static AppIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(typeof(AppIcon)));
    }

    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.Register(
            nameof(IconKey),
            typeof(string),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnIconChanged));

    public static readonly DependencyProperty UseBrandColorProperty =
        DependencyProperty.Register(
            nameof(UseBrandColor),
            typeof(bool),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnIconChanged));

    public static readonly DependencyProperty ResolvedGeometryProperty =
        DependencyProperty.Register(
            nameof(ResolvedGeometry),
            typeof(Geometry),
            typeof(AppIcon),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ResolvedDrawingProperty =
        DependencyProperty.Register(
            nameof(ResolvedDrawing),
            typeof(ImageSource),
            typeof(AppIcon),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsGeometryVisibleProperty =
        DependencyProperty.Register(
            nameof(IsGeometryVisible),
            typeof(bool),
            typeof(AppIcon),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsDrawingVisibleProperty =
        DependencyProperty.Register(
            nameof(IsDrawingVisible),
            typeof(bool),
            typeof(AppIcon),
            new PropertyMetadata(false));

    public string IconKey
    {
        get => (string)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public bool UseBrandColor
    {
        get => (bool)GetValue(UseBrandColorProperty);
        set => SetValue(UseBrandColorProperty, value);
    }

    public Geometry? ResolvedGeometry
    {
        get => (Geometry?)GetValue(ResolvedGeometryProperty);
        private set => SetValue(ResolvedGeometryProperty, value);
    }

    public ImageSource? ResolvedDrawing
    {
        get => (ImageSource?)GetValue(ResolvedDrawingProperty);
        private set => SetValue(ResolvedDrawingProperty, value);
    }

    public bool IsGeometryVisible
    {
        get => (bool)GetValue(IsGeometryVisibleProperty);
        private set => SetValue(IsGeometryVisibleProperty, value);
    }

    public bool IsDrawingVisible
    {
        get => (bool)GetValue(IsDrawingVisibleProperty);
        private set => SetValue(IsDrawingVisibleProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppIcon appIcon)
        {
            appIcon.UpdateIcon();
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        if (string.IsNullOrWhiteSpace(IconKey))
        {
            ResolvedGeometry = null;
            ResolvedDrawing = null;
            IsGeometryVisible = false;
            IsDrawingVisible = false;
            return;
        }

        var key = IconKey.Trim();

        // 1. If brand color is enabled, check for authentic DrawingImage first
        if (UseBrandColor)
        {
            var brandDrawing = TryFindResource($"BrandDrawing{key}") ??
                               TryFindResource($"BrandDrawing{MapAlias(key)}");

            if (brandDrawing is ImageSource imgSource)
            {
                ResolvedDrawing = imgSource;
                ResolvedGeometry = null;
                IsDrawingVisible = true;
                IsGeometryVisible = false;
                return;
            }
        }

        // 2. Look for Geometry vector resource
        var geoResource = TryFindResource($"Icon{key}") ??
                          TryFindResource($"Icon{MapAlias(key)}") ??
                          TryFindResource(key);

        if (geoResource is Geometry geo)
        {
            ResolvedGeometry = geo;
            ResolvedDrawing = null;
            IsGeometryVisible = true;
            IsDrawingVisible = false;
            return;
        }

        // 3. Fallback to DrawingImage if no geometry was matched
        var fallbackDrawing = TryFindResource($"BrandDrawing{key}") ??
                             TryFindResource($"BrandDrawing{MapAlias(key)}");

        if (fallbackDrawing is ImageSource fbImg)
        {
            ResolvedDrawing = fbImg;
            ResolvedGeometry = null;
            IsDrawingVisible = true;
            IsGeometryVisible = false;
            return;
        }

        // Default: empty
        ResolvedGeometry = null;
        ResolvedDrawing = null;
        IsGeometryVisible = false;
        IsDrawingVisible = false;
    }

    private static string MapAlias(string key) => key.ToLowerInvariant() switch
    {
        "dashboard" or "overview" => "Overview",
        "backups" or "backup" or "backupjobs" => "Backups",
        "database" or "sql" or "sqlserver" => "Database",
        "newjob" or "createjob" or "add" or "addcircle" => "CreateJob",
        "iissites" or "iis" or "globe" => "IisSites",
        "recovery" or "cloudrecovery" or "clouddownload" => "Recovery",
        "summary" or "analytics" or "history" or "trending" => "Summary",
        "logs" or "log" or "document" => "Logs",
        "notifications" or "alert" or "bell" => "Notifications",
        "settings" or "gear" or "engine" => "Settings",
        "email" or "smtp" or "mail" => "Email",
        "slack" => "Slack",
        "teams" or "microsoftteams" => "Teams",
        "discord" => "Discord",
        "telegram" => "Telegram",
        "pushover" => "Pushover",
        "ntfy" => "Ntfy",
        "pagerduty" => "PagerDuty",
        "customwebhook" or "webhook" or "code" => "CustomWebhook",
        "heartbeat" or "pulse" => "Heartbeat",
        "s3" or "awss3" or "aws" => "AwsS3",
        "googledrive" or "gdrive" => "GoogleDrive",
        "search" => "Search",
        "close" or "dismiss" or "exit" => "Close",
        "check" or "checkmark" => "Check",
        "checkcircle" => "CheckCircle",
        "warning" or "warningcircle" => "WarningCircle",
        "copy" => "Copy",
        "trash" or "delete" => "Trash",
        "folder" => "Folder",
        "server" => "Server",
        "play" => "Play",
        "clock" => "Clock",
        "shield" => "Shield",
        "eye" => "Eye",
        "eyeoff" => "EyeOff",
        "theme" or "darkmode" => "Theme",
        _ => key
    };
}
