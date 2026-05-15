using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace InterviewAssistant.App.Ui;

public partial class ToastOverlay : UserControl
{
    private static readonly FontFamily ToastFont = new("Segoe UI, Segoe UI Variable Text, sans-serif");
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(160);

    public ToastOverlay() => InitializeComponent();

    public void ClearAll()
    {
        ToastStack.Children.Clear();
    }

    public void Show(string message, ToastLevel level = ToastLevel.Success, int maxChars = 96)
    {
        var text = ToastMessages.Trim(message, maxChars);
        if (string.IsNullOrEmpty(text))
        {
            text = level switch
            {
                ToastLevel.Error => "Failed.",
                ToastLevel.Warning => "Warning.",
                ToastLevel.Info => "Note.",
                _ => "Done.",
            };
        }

        while (ToastStack.Children.Count > 3)
            ToastStack.Children.RemoveAt(0);

        var frame = CreateToastCard(text, level);
        ToastStack.Children.Add(frame);

        frame.Opacity = 0;
        frame.RenderTransform = new TranslateTransform(0, -6);
        frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn));
        ((TranslateTransform)frame.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-6, 0, FadeIn));

        var linger = level switch
        {
            ToastLevel.Error => TimeSpan.FromMilliseconds(4200),
            ToastLevel.Warning => TimeSpan.FromMilliseconds(3200),
            ToastLevel.Info => TimeSpan.FromMilliseconds(2600),
            _ => TimeSpan.FromMilliseconds(2400),
        };

        var timer = new DispatcherTimer { Interval = linger };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dismiss(frame);
        };
        timer.Start();
    }

    private static Border CreateToastCard(string text, ToastLevel level)
    {
        var theme = GetTheme(level);

        var icon = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = Brush(theme.IconBg),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = theme.Glyph,
                Foreground = Brushes.White,
                FontFamily = ToastFont,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };

        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush(theme.Text),
            FontFamily = ToastFont,
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 16,
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(label, TextRenderingMode.ClearType);

        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        icon.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(icon);
        row.Children.Add(label);

        return new Border
        {
            Background = Brush(theme.Background),
            BorderBrush = Brush(theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = row,
        };
    }

    private static ToastTheme GetTheme(ToastLevel level) => level switch
    {
        ToastLevel.Error => new ToastTheme(
            Background: "#FFF0F0",
            Border: "#F5C6CB",
            IconBg: "#D93025",
            Glyph: "×",
            Text: "#721C24"),
        ToastLevel.Warning => new ToastTheme(
            Background: "#FFF9E6",
            Border: "#FFEBA0",
            IconBg: "#E67E00",
            Glyph: "!",
            Text: "#856404"),
        ToastLevel.Info => new ToastTheme(
            Background: "#EFF6FF",
            Border: "#BFDBFE",
            IconBg: "#2563EB",
            Glyph: "i",
            Text: "#1E40AF"),
        _ => new ToastTheme(
            Background: "#ECFDF5",
            Border: "#A7F3D0",
            IconBg: "#059669",
            Glyph: "✓",
            Text: "#065F46"),
    };

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);

    private void Dismiss(Border frame)
    {
        if (!ToastStack.Children.Contains(frame))
            return;

        var fade = new DoubleAnimation(frame.Opacity, 0, FadeOut);
        fade.Completed += (_, _) => ToastStack.Children.Remove(frame);
        frame.BeginAnimation(OpacityProperty, fade);
    }

    private sealed record ToastTheme(
        string Background,
        string Border,
        string IconBg,
        string Glyph,
        string Text);
}
