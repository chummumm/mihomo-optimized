using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed class ProxyNodeTile : Button
{
    private readonly TextBlock name = Ui.Text("", 14);
    private readonly TextBlock latency = Ui.Text("", 20, true);
    private readonly TextBlock unit = Ui.Muted("ms", 12);
    private readonly TextBlock mean = Ui.Muted("", 12);
    private readonly TextBlock type = Ui.Muted("", 12);
    private readonly Border udp;
    private readonly Ellipse marker = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly LatencySparkline sparkline = new() { Height = 24, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Grid reading = new();
    private readonly Grid progress = new() { Height = 3, ClipToBounds = true, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Border probe = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform probeMove = new();
    private readonly Border flash = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Opacity = 0 };
    private string? stamp;
    private bool initialized, compact, inFlight;
    private double previousDelay;
    private static readonly DependencyProperty ReadingProperty = DependencyProperty.Register("Reading", typeof(double), typeof(ProxyNodeTile), new PropertyMetadata(0d, (owner, args) => ((ProxyNodeTile)owner).latency.Text = Math.Round((double)args.NewValue).ToString("0")));

    public ProxyNodeTile()
    {
        Style = ProxyVisuals.Style("Tile"); MinHeight = 116; MinWidth = 0; Padding = new Thickness(14, 12, 14, 10); Margin = new Thickness(0);
        name.TextWrapping = type.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = type.TextTrimming = TextTrimming.CharacterEllipsis;
        name.LineHeight = 20; name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        latency.LineHeight = 28; latency.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; latency.TextWrapping = TextWrapping.NoWrap;
        unit.Margin = new Thickness(2, 0, 0, 3); unit.VerticalAlignment = VerticalAlignment.Bottom;
        mean.Margin = new Thickness(6, 0, 0, 3); mean.VerticalAlignment = VerticalAlignment.Bottom; mean.TextWrapping = TextWrapping.NoWrap;
        mean.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        var root = new Grid();
        for (var row = 0; row < 4; row++) root.RowDefinitions.Add(new RowDefinition { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) }; DockPanel.SetDock(marker, Dock.Left); header.Children.Add(marker); header.Children.Add(name); root.Children.Add(header);
        reading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); reading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); reading.ColumnDefinitions.Add(new ColumnDefinition());
        reading.Children.Add(latency); Grid.SetColumn(unit, 1); reading.Children.Add(unit); Grid.SetColumn(mean, 2); reading.Children.Add(mean); reading.Margin = new Thickness(0, 0, 0, 4); Grid.SetRow(reading, 1); root.Children.Add(reading);
        sparkline.Margin = new Thickness(0, 0, 0, 4); Grid.SetRow(sparkline, 2); root.Children.Add(sparkline);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Height = 16 }; footer.Children.Add(type);
        var udpText = Ui.Muted("UDP", 10); udpText.FontFamily = new FontFamily("Cascadia Mono, Consolas"); udpText.LineHeight = 16; udpText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        udp = new Border { Child = udpText, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(3), Margin = new Thickness(6, 0, 0, 0) }; udp.SetResourceReference(Border.BackgroundProperty, "SubtleHoverBrush"); footer.Children.Add(udp); Grid.SetRow(footer, 3); root.Children.Add(footer);
        probe.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); probe.RenderTransform = probeMove; progress.Children.Add(probe);
        Grid.SetRowSpan(flash, 4); root.Children.Add(flash); Grid.SetRowSpan(progress, 4); root.Children.Add(progress); Content = root;
        UpdateInsets(); ProxyVisuals.Press(this, .96); ProxyVisuals.Surface(this, "CardHoverBrush", "SubtleSurfaceBrush");
        SizeChanged += (_, _) => AnimateProbe(); IsVisibleChanged += (_, _) => AnimateProbe();
    }

    internal void SetCompact(bool value)
    {
        if (compact == value) return; compact = value;
        MinHeight = value ? 104 : 116; Padding = value ? new Thickness(12, 10, 12, 8) : new Thickness(14, 12, 14, 10);
        latency.LineHeight = value ? 24 : 28; latency.FontSize = previousDelay > 0 ? value ? 18 : 20 : 14; UpdateInsets();
    }
    internal void SetProvider(bool value) => SetResourceReference(BackgroundProperty, value ? "SubtleSurfaceBrush" : "SurfaceBrush");
    private void UpdateInsets()
    {
        flash.Margin = new Thickness(-Padding.Left - 1, -Padding.Top - 1, -Padding.Right - 1, -Padding.Bottom - 1);
        progress.Margin = new Thickness(-Padding.Left, 0, -Padding.Right, -Padding.Bottom);
    }
    public void Update(ProxyNodeInfo value, bool testing)
    {
        name.Text = value.Name; type.Text = value.Type; udp.Visibility = value.Udp ? Visibility.Visible : Visibility.Collapsed;
        var changed = initialized && previousDelay != value.Delay;
        if (!initialized || changed)
        {
            var from = (double)GetValue(ReadingProperty); BeginAnimation(ReadingProperty, null); SetValue(ReadingProperty, value.Delay);
            if (value.Delay > 0 && previousDelay > 0 && changed && ProxyVisuals.Animate)
                BeginAnimation(ReadingProperty, new DoubleAnimation(from, value.Delay, TimeSpan.FromMilliseconds(700)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
            else latency.Text = value.Delay > 0 ? value.Delay.ToString("0.#") : value.DelayLabel;
        }
        latency.FontSize = value.Delay > 0 ? compact ? 18 : 20 : 14; latency.FontWeight = value.Delay > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        unit.Visibility = value.Delay > 0 ? Visibility.Visible : Visibility.Collapsed;
        var latest = value.History.LastOrDefault(); mean.Text = latest?.Mean > 0 ? $"({latest.Mean:0.#}ms)" : "";
        var tone = testing ? "warn" : value.Tone;
        marker.SetResourceReference(Shape.StrokeProperty, ProxyVisuals.Tone(tone)); marker.StrokeThickness = tone == "idle" ? 1 : 0;
        if (tone == "idle") marker.Fill = Brushes.Transparent; else marker.SetResourceReference(Shape.FillProperty, ProxyVisuals.Tone(tone));
        name.SetResourceReference(TextBlock.ForegroundProperty, value.Delay <= 0 ? "TextSecondaryBrush" : "TextPrimaryBrush");
        latency.SetResourceReference(TextBlock.ForegroundProperty, value.Tone == "bad" ? "DangerBrush" : value.Delay > 0 ? "TextPrimaryBrush" : "TextSecondaryBrush");
        reading.Opacity = testing ? .6 : 1; inFlight = testing; progress.Visibility = testing ? Visibility.Visible : Visibility.Collapsed; AnimateProbe();
        // The reference keeps the tile's appearance while probing; its request guard prevents repeats.
        IsEnabled = true; IsHitTestVisible = value.CanTest; Focusable = value.CanTest;
        ToolTip = value.Name + "\n" + value.Type + (value.Udp ? " · UDP" : "") + "\n" + (testing ? L.T("正在测速…") : value.DelayLabel);
        AutomationProperties.SetName(this, value.Name + " · " + (testing ? L.T("正在测速…") : value.DelayLabel));
        sparkline.Set(value.History.Select(item => item.Delay > 0 ? Math.Min(1000, item.Delay) : 1000).ToArray(), value.Tone);
        if (initialized && latest?.Stamp != stamp) Pulse(ProxyVisuals.Tone(value.Tone));
        if (changed) ProxyVisuals.Reading(latency);
        stamp = latest?.Stamp; previousDelay = value.Delay; initialized = true;
    }
    private void AnimateProbe()
    {
        probeMove.BeginAnimation(TranslateTransform.XProperty, null);
        var width = Math.Max(0, ActualWidth - 2); probe.Width = width * .4;
        if (!inFlight || !IsVisible || width <= 0) return;
        if (!ProxyVisuals.Animate) { probeMove.X = width * .3; return; }
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500), RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-width * .4, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(width, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500)), new KeySpline(.4, 0, .6, 1)));
        probeMove.BeginAnimation(TranslateTransform.XProperty, animation);
    }
    public void Reveal() { BringIntoView(); Focus(); Pulse("AccentBrush", 1200); }
    private void Pulse(string resource, int duration = 1000)
    {
        flash.SetResourceReference(Border.BorderBrushProperty, resource);
        ProxyVisuals.Tween(flash, OpacityProperty, ProxyVisuals.Animate ? 1 : 0, 0, duration);
    }
    private sealed class LatencySparkline : FrameworkElement
    {
        private double[] values = [0, 0], prior = [0, 0];
        private string tone = "idle";
        private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(LatencySparkline), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
        public void Set(double[] samples, string state)
        {
            var next = samples.Length > 1 ? samples : new[] { samples.FirstOrDefault(), samples.FirstOrDefault() }; tone = state;
            if (next.SequenceEqual(values)) { InvalidateVisual(); return; }
            var progress = (double)GetValue(ProgressProperty); prior = values.Length == prior.Length ? values.Select((value, index) => prior[index] + (value - prior[index]) * progress).ToArray() : values;
            values = next; BeginAnimation(ProgressProperty, null); SetValue(ProgressProperty, 1d);
            if (prior.Length == values.Length) ProxyVisuals.Tween(this, ProgressProperty, 0, 1, 900); else prior = values;
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing); if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var resource = tone == "bad" ? "DangerBrush" : tone == "warn" ? "WarningBrush" : "AccentBrush";
            var brush = (Brush?)TryFindResource(resource) ?? Brushes.Gray; var progress = (double)GetValue(ProgressProperty);
            var inset = 2 * ActualHeight / 26;
            var points = values.Select((value, index) => new Point(index * ActualWidth / (values.Length - 1), ActualHeight - inset - Math.Clamp((prior.Length == values.Length ? prior[index] + (value - prior[index]) * progress : value) / 1000, 0, 1) * (ActualHeight - inset * 2))).ToArray();
            StreamGeometry Path(bool fill)
            {
                var path = new StreamGeometry(); using var context = path.Open(); context.BeginFigure(points[0], fill, fill);
                for (var index = 1; index < points.Length; index++) context.QuadraticBezierTo(points[index - 1], new Point((points[index - 1].X + points[index].X) / 2, (points[index - 1].Y + points[index].Y) / 2), true, false);
                context.LineTo(points[^1], true, false); if (fill) { context.LineTo(new Point(ActualWidth, ActualHeight), false, false); context.LineTo(new Point(0, ActualHeight), false, false); }
                return path;
            }
            if (brush is SolidColorBrush solid)
            {
                var gradient = new LinearGradientBrush(Color.FromArgb((byte)(solid.Color.A * .3), solid.Color.R, solid.Color.G, solid.Color.B), Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B), 90);
                drawing.DrawGeometry(gradient, null, Path(true));
            }
            drawing.DrawGeometry(null, new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Path(false));
        }
    }
}

/// <summary>The reference's CSS grid: 6 px gaps, 196 px node columns or 180 px member columns.</summary>
internal sealed class ProxyTilePanel : Panel
{
    internal double PreferredWidth { get; set; } = 196;
    internal double CompactAt { get; set; } = 640;
    internal double RowHeight { get; set; }
    internal int Columns { get; private set; } = 1;
    private readonly Dictionary<UIElement, (Point Position, TranslateTransform Move)> placements = [];
    private double rowHeight;
    internal event Action? Arranged;
    private int ColumnCount(double width) => ProxyVisuals.ContainerWidth(this, width) <= CompactAt ? 2 : Math.Max(1, (int)Math.Floor((width + 6) / (PreferredWidth + 6)));
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 700; Columns = ColumnCount(width); var cell = Math.Max(0, (width - (Columns - 1) * 6) / Columns);
        rowHeight = RowHeight;
        foreach (UIElement child in InternalChildren) { if (child is ProxyNodeTile tile) tile.SetCompact(ProxyVisuals.ContainerWidth(this, width) <= 640); child.Measure(new Size(cell, double.PositiveInfinity)); rowHeight = Math.Max(rowHeight, child.DesiredSize.Height); }
        var rows = Math.Ceiling((double)InternalChildren.Count / Columns); return new Size(width, Math.Max(0, rows * rowHeight + Math.Max(0, rows - 1) * 6));
    }
    protected override Size ArrangeOverride(Size final)
    {
        Columns = ColumnCount(final.Width); var cell = Math.Max(0, (final.Width - (Columns - 1) * 6) / Columns);
        foreach (var stale in placements.Keys.Where(item => !InternalChildren.Contains(item)).ToArray()) placements.Remove(stale);
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var child = InternalChildren[index]; var point = new Point(index % Columns * (cell + 6), index / Columns * (rowHeight + 6)); child.Arrange(new Rect(point, new Size(cell, rowHeight)));
            if (!placements.TryGetValue(child, out var prior))
            {
                var move = new TranslateTransform(); var transforms = new TransformGroup(); if (child.RenderTransform != Transform.Identity) transforms.Children.Add(child.RenderTransform); transforms.Children.Add(move); child.RenderTransform = transforms;
                placements[child] = (point, move);
            }
            else
            {
                if (prior.Position != point && ProxyVisuals.Animate)
                {
                    ProxyVisuals.Tween(prior.Move, TranslateTransform.XProperty, prior.Move.X + prior.Position.X - point.X, 0, 250);
                    ProxyVisuals.Tween(prior.Move, TranslateTransform.YProperty, prior.Move.Y + prior.Position.Y - point.Y, 0, 250);
                }
                placements[child] = (point, prior.Move);
            }
        }
        Arranged?.Invoke(); return final;
    }
}
