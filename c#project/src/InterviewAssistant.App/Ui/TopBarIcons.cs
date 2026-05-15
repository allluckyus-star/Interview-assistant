using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ShapesPath = System.Windows.Shapes.Path;

namespace InterviewAssistant.App.Ui;

/// <summary>SVG icons from live.py rendered as WPF Path icons (cyan top bar).</summary>
public static class TopBarIcons
{
    public const string CyanBg = "#b2ebf2";
    public const string CyanHover = "#80deea";
    public const string CloseHover = "#ff8a80";

    public static ShapesPath CreateCopyGlyphIcon(double size = 14, string color = "#455a64") =>
        PathFromSvg(
            size,
            "M8.5,8.5H19.5A1.5,1.5,0,0,1,21,10V21A1.5,1.5,0,0,1,19.5,22.5H8.5A1.5,1.5,0,0,1,7,21V10A1.5,1.5,0,0,1,8.5,8.5z M4,15H3A1.5,1.5,0,0,1,1.5,13.5V4A1.5,1.5,0,0,1,3,2.5H12A1.5,1.5,0,0,1,13.5,4V5",
            color,
            "0 0 24 24",
            stroke: true);

    public static ShapesPath CreateCancelIcon(double size = 14, string color = "#c62828")
    {
        return PathFromSvg(
            size,
            "M18 6L6 18M6 6l12 12",
            color,
            "0 0 24 24",
            stroke: true);
    }

    public static ShapesPath CreateSendPlaneIcon(double size = 14, string color = "#1565c0")
    {
        return PathFromSvg(
            size,
            "M2 21l23-9L2 3v7l15 2-15 2v7z",
            color,
            "0 0 24 24",
            filled: true);
    }

    public static ShapesPath CreateCloseIcon(double size = 16, string color = "#111111")
    {
        return PathFromSvg(
            size,
            $"M3.72 3.72a.75.75 0 0 1 1.06 0L8 6.94l3.22-3.22a.75.75 0 1 1 1.06 1.06L9.06 8l3.22 3.22a.75.75 0 1 1-1.06 1.06L8 9.06l-3.22 3.22a.75.75 0 0 1-1.06-1.06L6.94 8 3.72 4.78a.75.75 0 0 1 0-1.06z",
            color,
            "0 0 16 16");
    }

    public static ShapesPath CreateSaveIcon(double size = 16, string color = "#111111")
    {
        return PathFromSvg(
            size,
            "M8 1 4 6h2.5v5h3V6H12L8 1zM2 12.5h12V15H2v-2.5z",
            color,
            "0 0 16 16");
    }

    /// <summary>Photo / gallery glyph (framed landscape with hills).</summary>
    public static ShapesPath CreateImageIcon(double size = 14, string color = "#111111") =>
        PathFromSvg(
            size,
            "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z",
            color,
            "0 0 24 24",
            filled: true);

    public static ShapesPath CreateTextGlyphIcon(double size = 14, string color = "#111111") =>
        PathFromSvg(
            size,
            "M11 4 L5 20 H7.8 L8.9 17 H15.1 L16.2 20 H19 L13 4 H11 Z M10.5 16 L12 10.3 13.5 16 H10.5 Z",
            color,
            "0 0 24 24",
            filled: true);

    public static ShapesPath CreateFolderIcon(double size = 14, string color = "#111111") =>
        PathFromSvg(
            size,
            "M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z",
            color,
            "0 0 24 24",
            filled: true);

    public static ShapesPath CreateKebabIcon(double size = 16, string color = "#111111")
    {
        var g = new GeometryGroup();
        g.Children.Add(new EllipseGeometry(new Point(8, 3), 1.35, 1.35));
        g.Children.Add(new EllipseGeometry(new Point(8, 8), 1.35, 1.35));
        g.Children.Add(new EllipseGeometry(new Point(8, 13), 1.35, 1.35));
        return new ShapesPath
        {
            Data = g,
            Fill = NewBrush(color),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
        };
    }

    public static ShapesPath CreateReadModeIcon(double size = 16, string color = "#222222")
    {
        return PathFromSvg(
            size,
            "M8 2.5h10.5c.83 0 1.5.67 1.5 1.5v16c0 .83-.67 1.5-1.5 1.5H8A2.5 2.5 0 0 1 5.5 19V5A2.5 2.5 0 0 1 8 2.5zm1.5 5.25h7v1.5h-7zm0 3.5h7v1.5h-7zm0 3.5h4.5v1.5H9.5z",
            color,
            "0 0 24 24");
    }

    public static ShapesPath CreateTypeModeIcon(double size = 16, string color = "#222222")
    {
        return PathFromSvg(
            size,
            "M3 17.46V20.5c0 .28.22.5.5.5h3.04c.13 0 .26-.05.35-.15L17.81 9.94l-3.59-3.59L3.35 17.11c-.09.09-.15.22-.15.35zM20.71 7.04a1.003 1.003 0 0 0 0-1.41l-2.34-2.34a1.003 1.003 0 0 0-1.41 0l-1.83 1.83 3.59 3.59 1.99-1.67z",
            color,
            "0 0 24 24");
    }

    public static ShapesPath CreateBehavioralModeIcon(double size = 16, string color = "#222222")
    {
        return PathFromSvg(
            size,
            "M5 4.5h8.5A2.25 2.25 0 0 1 15.75 6.75v3.5A2.25 2.25 0 0 1 13.5 12.5h-1.9L9.25 15.5V12.5H5A2.25 2.25 0 0 1 2.75 10.25v-3.5A2.25 2.25 0 0 1 5 4.5z",
            color,
            "0 0 24 24");
    }

    public static ShapesPath CreateInfoModeIcon(double size = 16, string color = "#222222")
    {
        return PathFromSvg(
            size,
            "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z",
            color,
            "0 0 24 24");
    }

    public static ShapesPath CreateFreeModeIcon(double size = 16, string color = "#222222")
    {
        return PathFromSvg(
            size,
            "M20 5H4c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V7c0-1.1-.9-2-2-2zm0 12H4V7h16v10zM6 10h2v2H6v-2zm3 0h8v2H9v-2zm-3 4h2v2H6v-2zm3 0h5v2H9v-2z",
            color,
            "0 0 24 24");
    }

    public static ShapesPath PathFromSvg(
        double size,
        string pathData,
        string color,
        string viewBox,
        bool stroke = false,
        bool filled = false)
    {
        var parts = viewBox.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var w = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw)
            ? vw
            : 16.0;
        var h = parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh)
            ? vh
            : 16.0;
        var geo = Geometry.Parse(pathData).Clone();
        geo.Transform = new ScaleTransform(size / w, size / h);
        var path = new ShapesPath
        {
            Data = geo,
            Stretch = Stretch.None,
            Width = size,
            Height = size,
        };
        if (stroke)
        {
            path.Stroke = NewBrush(color);
            path.StrokeThickness = 2.2;
            path.Fill = null;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
        }
        else
        {
            path.Fill = NewBrush(color);
        }

        _ = filled;
        return path;
    }

    private static SolidColorBrush NewBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);
}
