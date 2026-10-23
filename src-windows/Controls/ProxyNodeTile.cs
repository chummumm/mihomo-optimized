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
    private readonly TextBlock latency = Ui.Text("", 22, true);
    private readonly TextBlock mean = Ui.Muted("");
    private readonly TextBlock type = Ui.Muted("");
    private readonly TextBlock udp = Ui.Muted("UDP", 10);
    private readonly Ellipse marker = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly LatencySparkline sparkline = new() { Height = 26, Margin = new Thickness(0, 2, 0, 3) };
    private readonly ProgressBar progress = new() { Height = 3, Visibility = Visibility.Collapsed, IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-12, 0, -12, -10) };
    private readonly Border flash = new() { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Opacity = 0, Margin = new Thickness(-12, -10, -12, -10) };
    private string? stamp;
    private bool initialized;
    public ProxyNodeTile()
    {
        MinHeight = 122; MinWidth = 0; Padding = new Thickness(12, 10, 12, 10); Margin = new Thickness(3); HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(BorderBrushProperty, "BorderBrush");
        name.TextWrapping = type.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = type.TextTrimming = TextTrimming.CharacterEllipsis;
        var root = new Grid();
        for (var row = 0; row < 4; row++) root.RowDefinitions.Add(new RowDefinition { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new DockPanel(); DockPanel.SetDock(marker, Dock.Left); header.Children.Add(marker); header.Children.Add(name); root.Children.Add(header);
        var reading = new StackPanel { Orientation = Orientation.Horizontal }; reading.Children.Add(latency); mean.Margin = new Thickness(6, 0, 0, 0); reading.Children.Add(mean); Grid.SetRow(reading, 1); root.Children.Add(reading);
        Grid.SetRow(sparkline, 2); root.Children.Add(sparkline);
        var footer = new DockPanel(); DockPanel.SetDock(udp, Dock.Right); footer.Children.Add(udp); footer.Children.Add(type); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Grid.SetRowSpan(flash, 4); root.Children.Add(flash); Grid.SetRowSpan(progress, 4); root.Children.Add(progress); Content = root;
    }

    public void Update(ProxyNodeInfo value, bool testing)
    {
        name.Text = value.Name; type.Text = value.Type; udp.Visibility = value.Udp ? Visibility.Visible : Visibility.Collapsed;
        latency.Text = value.DelayLabel; latency.FontSize = value.Delay > 0 ? 22 : 14; latency.FontWeight = value.Delay > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        var latest = value.History.LastOrDefault(); mean.Text = latest?.Mean > 0 ? $"({latest.Mean:0.#} ms)" : "";
        marker.SetResourceReference(Shape.FillProperty, testing ? "WarningBrush" : ToneResource(value.Tone));
        latency.SetResourceReference(TextBlock.ForegroundProperty, value.Failed ? "DangerBrush" : "TextPrimaryBrush");
        progress.Visibility = testing ? Visibility.Visible : Visibility.Collapsed; progress.IsIndeterminate = testing && SystemParameters.ClientAreaAnimation;
        progress.Value = testing ? 50 : 0; IsEnabled = value.CanTest && !testing;
        ToolTip = value.Name + "\n" + value.Type + (value.Udp ? " · UDP" : "") + "\n" + (testing ? L.T("正在测速…") : value.DelayLabel);
        AutomationProperties.SetName(this, value.Name + " · " + (testing ? L.T("正在测速…") : value.DelayLabel));
        sparkline.Set(value.History.Select(reading => reading.Delay > 0 ? Math.Min(1000, reading.Delay) : 1000).ToArray(), value.Tone);
        if (initialized && latest?.Stamp != stamp) Pulse(ToneResource(value.Tone));
        stamp = latest?.Stamp; initialized = true;
    }

    public void Reveal() { BringIntoView(); Focus(); Pulse("AccentBrush"); }
    private void Pulse(string resource)
    {
        flash.SetResourceReference(Border.BorderBrushProperty, resource);
        if (SystemParameters.ClientAreaAnimation) flash.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1200)));
        else { flash.BeginAnimation(OpacityProperty, null); flash.Opacity = 1; }
    }
    private static string ToneResource(string tone) => tone switch { "good" => "SuccessBrush", "warn" => "WarningBrush", "bad" => "DangerBrush", _ => "TextSecondaryBrush" };
    private sealed class LatencySparkline : FrameworkElement
    {
        private double[] values = [];
        private string tone = "idle";
        public void Set(double[] samples, string state) { values = samples.Length > 1 ? samples : [samples.FirstOrDefault(), samples.FirstOrDefault()]; tone = state; InvalidateVisual(); }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing); if (ActualWidth <= 0 || ActualHeight <= 0 || values.Length < 2) return;
            var resource = tone == "bad" ? "DangerBrush" : tone == "warn" ? "WarningBrush" : "AccentBrush";
            var brush = (Brush?)TryFindResource(resource) ?? Brushes.Gray;
            var points = values.Select((value, index) => new Point(index * ActualWidth / (values.Length - 1), ActualHeight - 2 - Math.Clamp(value / 1000, 0, 1) * (ActualHeight - 4))).ToArray();
            var path = new StreamGeometry(); using (var context = path.Open())
            {
                context.BeginFigure(points[0], false, false);
                for (var index = 1; index < points.Length; index++)
                {
                    var half = (points[index].X - points[index - 1].X) / 2;
                    context.BezierTo(new Point(points[index - 1].X + half, points[index - 1].Y), new Point(points[index].X - half, points[index].Y), points[index], true, false);
                }
            }
            path.Freeze(); drawing.DrawGeometry(null, new Pen(brush, 1.4), path);
        }
    }
}

/// <summary>Equal-width cards with the reference layout's 196 px preferred column width.</summary>
internal sealed class ProxyTilePanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 700;
        var columns = Math.Max(1, (int)Math.Floor(width / 196)); var cell = width / columns;
        var height = 0d;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(cell, double.PositiveInfinity)); height = Math.Max(height, child.DesiredSize.Height); }
        return new Size(width, Math.Ceiling((double)InternalChildren.Count / columns) * height);
    }
    protected override Size ArrangeOverride(Size final)
    {
        var columns = Math.Max(1, (int)Math.Floor(final.Width / 196)); var cell = final.Width / columns;
        var height = InternalChildren.Cast<UIElement>().Select(child => child.DesiredSize.Height).DefaultIfEmpty(0).Max();
        for (var index = 0; index < InternalChildren.Count; index++) InternalChildren[index].Arrange(new Rect(index % columns * cell, index / columns * height, cell, height));
        return final;
    }
}
