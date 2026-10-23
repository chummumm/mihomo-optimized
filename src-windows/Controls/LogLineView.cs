using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mihomo.Desktop.Pages;

namespace Mihomo.Desktop.Controls;

/// <summary>The three-column, wrapping mono row from Logs/style.css.</summary>
public sealed class LogLineView : UserControl
{
    private readonly Grid layout = new();
    private readonly Border inset;
    private readonly Border errorBackground = new() { Opacity = .6, IsHitTestVisible = false };
    private readonly Border hoverBackground = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock time = Ui.Text("", 12);
    private readonly TrackedLevel level = new();
    private readonly TextBlock payload = Ui.Text("", 12);
    private readonly Border lamp = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4), CornerRadius = new CornerRadius(0, 2, 2, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private LogsPage.LogRow? row;
    private bool listening;
    private bool narrow;

    public LogLineView()
    {
        FontFamily = new FontFamily("Cascadia Mono, Cascadia Code, Consolas, Microsoft YaHei UI");
        time.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
        foreach (var text in new[] { time, payload })
        { text.LineHeight = 19.8; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; text.VerticalAlignment = VerticalAlignment.Top; }
        time.TextWrapping = TextWrapping.NoWrap; payload.TextWrapping = TextWrapping.Wrap;
        layout.Children.Add(time); layout.Children.Add(level); layout.Children.Add(payload);
        inset = new Border { Child = layout, Padding = new Thickness(18, 3, 18, 3) };
        var root = new Grid(); root.Children.Add(errorBackground); root.Children.Add(hoverBackground); root.Children.Add(inset); root.Children.Add(lamp); Content = root;
        errorBackground.SetResourceReference(Border.BackgroundProperty, "CriticalBackgroundBrush");
        hoverBackground.SetResourceReference(Border.BackgroundProperty, "SubtleHoverBrush");
        lamp.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        MouseEnter += (_, _) => hoverBackground.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => hoverBackground.Visibility = Visibility.Collapsed;
        DataContextChanged += (_, args) =>
        {
            Detach(); row = args.NewValue as LogsPage.LogRow; Attach(); ApplyRow();
        };
        Loaded += (_, _) => { Attach(); Adapt(true); AnimateNewest(); };
        Unloaded += (_, _) => { Detach(); StopArrival(); };
        SizeChanged += (_, _) => Adapt();
        Adapt(true);
    }
    private void Attach()
    {
        if (listening || row is null || !IsLoaded) return;
        PropertyChangedEventManager.AddHandler(row, Changed, nameof(LogsPage.LogRow.IsNewest)); listening = true;
    }
    private void Detach()
    {
        if (listening && row is not null) PropertyChangedEventManager.RemoveHandler(row, Changed, nameof(LogsPage.LogRow.IsNewest));
        listening = false;
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) => AnimateNewest();
    private void ApplyRow()
    {
        time.Text = row?.Time ?? ""; level.Text = row?.Level ?? ""; payload.Text = row?.Message ?? "";
        RefreshColors(); AnimateNewest();
    }
    internal void RefreshColors()
    {
        errorBackground.Visibility = row?.Level == "ERROR" ? Visibility.Visible : Visibility.Collapsed;
        level.SetResourceReference(TrackedLevel.ForegroundProperty, row?.Level switch
        { "INFO" => "AccentBrush", "WARNING" => "WarningBrush", "ERROR" => "DangerBrush", _ => "TextTertiaryBrush" });
        if (row?.Level == "WARNING" && TryFindResource("WarningBrush") is SolidColorBrush warning && TryFindResource("TextPrimaryBrush") is SolidColorBrush text)
            payload.Foreground = new SolidColorBrush(Mix(warning.Color, text.Color, .3));
        else payload.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        if (TryFindResource("AccentBrush") is SolidColorBrush accent)
            lamp.Effect = new DropShadowEffect { Color = accent.Color, BlurRadius = 8, ShadowDepth = 0, Opacity = 1 };
    }
    private void Adapt(bool force = false)
    {
        bool compact = (Window.GetWindow(this)?.ActualWidth ?? 1180) <= 768;
        if (!force && compact == narrow) return;
        narrow = compact; layout.ColumnDefinitions.Clear(); layout.RowDefinitions.Clear();
        inset.Padding = compact ? new Thickness(12, 6, 12, 6) : new Thickness(18, 3, 18, 3);
        time.FontSize = compact ? 10 : 12; time.LineHeight = compact ? 16.5 : 19.8;
        if (compact)
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(time, 0); Grid.SetColumnSpan(time, 3); Grid.SetRow(time, 0);
            Grid.SetColumn(level, 0); Grid.SetRow(level, 2); Grid.SetColumn(payload, 2); Grid.SetRow(payload, 2);
        }
        else
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(time, 0); Grid.SetColumnSpan(time, 1); Grid.SetRow(time, 0);
            Grid.SetColumn(level, 2); Grid.SetRow(level, 0); Grid.SetColumn(payload, 4); Grid.SetRow(payload, 0);
        }
        level.Margin = new Thickness(0, 2, 0, 0);
    }
    private void AnimateNewest()
    {
        StopArrival();
        if (!IsLoaded || row is not { IsNewest: true } || !FluentMotion.Enabled) return;
        double elapsed = Stopwatch.GetElapsedTime(row.ArrivedAt).TotalMilliseconds;
        if (elapsed >= 1200) return;
        lamp.Visibility = Visibility.Visible;
        lamp.Opacity = 0;
        SettingsSegments.Animate(lamp, OpacityProperty, 1, 0, Math.Max(1, 1200 - elapsed));
        if (elapsed >= 420) return;
        var shift = new TranslateTransform(); RenderTransform = shift;
        SettingsSegments.Animate(this, OpacityProperty, 0, 1, Math.Max(1, 420 - elapsed));
        SettingsSegments.Animate(shift, TranslateTransform.YProperty, 6, 0, Math.Max(1, 420 - elapsed));
    }
    private void StopArrival()
    {
        BeginAnimation(OpacityProperty, null); Opacity = 1;
        RenderTransform = Transform.Identity;
        lamp.BeginAnimation(OpacityProperty, null); lamp.Visibility = Visibility.Collapsed;
    }
    internal static Color Mix(Color first, Color second, double proportion) => Color.FromArgb(
        (byte)Math.Round(first.A * proportion + second.A * (1 - proportion)),
        (byte)Math.Round(first.R * proportion + second.R * (1 - proportion)),
        (byte)Math.Round(first.G * proportion + second.G * (1 - proportion)),
        (byte)Math.Round(first.B * proportion + second.B * (1 - proportion)));

    /// <summary>WPF TextBlock has no tracking property; the original level label uses 0.06em.</summary>
    private sealed class TrackedLevel : FrameworkElement
    {
        internal static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(TrackedLevel), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
        private Brush Foreground => (Brush)GetValue(ForegroundProperty);
        private string text = "";
        internal string Text { get => text; set { text = value; InvalidateMeasure(); InvalidateVisual(); } }
        private FormattedText Letter(char value) => new(value.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Mono, Cascadia Code, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        protected override Size MeasureOverride(Size availableSize) => new(text.Sum(character => Letter(character).WidthIncludingTrailingWhitespace + .6), 16.5);
        protected override void OnRender(DrawingContext drawing)
        {
            double x = 0;
            foreach (char character in text) { var letter = Letter(character); drawing.DrawText(letter, new Point(x, 0)); x += letter.WidthIncludingTrailingWhitespace + .6; }
        }
    }
}
