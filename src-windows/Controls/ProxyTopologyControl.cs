using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>A WPF traffic graph. Every interactive box remains a native, keyboard-accessible button.</summary>
internal sealed class ProxyTopologyControl : Grid
{
    private readonly Canvas boxes = new();
    private readonly LinkSurface lines;
    private readonly Dictionary<string, NodeView> views = new(StringComparer.Ordinal);
    private readonly TextBlock[] headings = [Ui.Label("来源", 12), Ui.Label("策略组", 12), Ui.Label("出口", 12)];
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TopologyModel model = TopologyModel.Empty;
    private bool active;
    private string? hovered;
    private double lastFrame;
    private double boxWidth = 180;
    private double boxHeight = 42;
    public event Action<string, string>? Picked;
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    public ProxyTopologyControl()
    {
        this.Id("ProxyTopology"); ClipToBounds = false; MinHeight = 194;
        lines = new LinkSurface(this); Children.Add(lines); Children.Add(boxes);
        foreach (var heading in headings) { heading.IsHitTestVisible = false; heading.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); boxes.Children.Add(heading); }
        SizeChanged += (_, _) => { Place(); Wake(); };
        IsVisibleChanged += (_, _) => Wake();
        timer.Tick += (_, _) => Tick();
        L.Changed += () => { UpdateLabels(); lines.InvalidateVisual(); };
    }

    public void SetActive(bool value)
    {
        active = value;
        if (!value) { timer.Stop(); hovered = null; foreach (var view in views.Values) { view.X = view.TargetX; view.Y = view.TargetY; Canvas.SetLeft(view.Button, view.X); Canvas.SetTop(view.Button, view.Y); } }
        Wake();
    }

    public void SetModel(TopologyModel value)
    {
        model = value;
        var desired = value.Nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in views.Keys.Where(id => !desired.Contains(id)).ToArray()) { boxes.Children.Remove(views[id].Button); views.Remove(id); }
        foreach (var node in value.Nodes)
        {
            if (!views.TryGetValue(node.Id, out var view))
            {
                view = new NodeView(node); views[node.Id] = view; boxes.Children.Add(view.Button);
                var id = node.Id;
                view.Button.MouseEnter += (_, _) => SetHover(id); view.Button.MouseLeave += (_, _) => SetHover(null);
                view.Button.GotKeyboardFocus += (_, _) => SetHover(id); view.Button.LostKeyboardFocus += (_, _) => SetHover(null);
                view.Button.Click += (_, _) => { var current = views.GetValueOrDefault(id)?.Node; if (current?.Target is { } target) Picked?.Invoke(current.Kind == "group" ? "focus-group" : "focus-node", target); };
            }
            var before = view.Node.Stamp; view.Node = node;
            view.Title.Text = node.Title; view.Subtitle.Text = node.Subtitle;
            view.Button.ToolTip = node.Title + "\n" + node.Subtitle;
            AutomationProperties.SetName(view.Button, node.Title + " · " + node.Subtitle);
            view.Button.Focusable = node.Target is not null;
            view.Button.Cursor = node.Target is not null ? Cursors.Hand : Cursors.Arrow;
            view.Button.Id(node.Kind == "group" ? "TopologyGroup_" + node.Title : node.Kind == "exit" ? "TopologyNode_" + node.Title : "TopologySource_" + node.Title);
            view.Marker.SetResourceReference(Shape.FillProperty, ToneResource(node.Tone));
            view.Marker.RadiusX = view.Marker.RadiusY = node.Kind == "group" ? 2 : 4;
            if (node.Kind == "group" && node.GroupColor.HasValue)
            {
                var resource = "Line" + node.GroupColor.Value + "Brush";
                if (TryFindResource(resource) is Brush) view.Marker.SetResourceReference(Shape.FillProperty, resource);
            }
            if (before is not null && node.Stamp != before && SystemParameters.ClientAreaAnimation)
            {
                view.Flash.SetResourceReference(Border.BorderBrushProperty, ToneResource(node.Tone));
                view.Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(900)));
            }
        }
        Height = 32 + Math.Max(3, value.Rows) * (ActualWidth < 620 ? 46 : 54);
        Place(); UpdateLabels(); Wake();
    }

    public void RefreshPalette() { lines.InvalidateVisual(); Wake(); }
    private static string ToneResource(string tone) => tone switch { "good" => "SuccessBrush", "warn" => "WarningBrush", "bad" => "DangerBrush", "live" => "AccentBrush", _ => "TextSecondaryBrush" };
    private void UpdateLabels()
    {
        foreach (var view in views.Values) view.Button.ToolTip = view.Node.Title + "\n" + view.Node.Subtitle;
        AutomationProperties.SetName(this, L.F("实时流量拓扑，{0} 个连接", model.Connections));
    }

    private void Place()
    {
        if (ActualWidth <= 0) return;
        var narrow = ActualWidth < 620;
        boxWidth = narrow ? Math.Max(78, (ActualWidth - 76) / 3) : Math.Min(200, (ActualWidth - 4) / 4.3);
        boxHeight = narrow ? 36 : 42;
        var xs = new[] { 2d, (ActualWidth - boxWidth) / 2, ActualWidth - 2 - boxWidth };
        Height = 32 + Math.Max(3, model.Rows) * (narrow ? 46 : 54);
        for (var column = 0; column < 3; column++)
        {
            Canvas.SetLeft(headings[column], xs[column]); Canvas.SetTop(headings[column], 0); headings[column].MaxWidth = boxWidth;
            var members = model.Nodes.Where(item => item.Column == column).ToArray();
            for (var index = 0; index < members.Length; index++)
            {
                var view = views[members[index].Id]; view.TargetX = xs[column]; view.TargetY = 26 + (Height - 30) * (index + .5) / members.Length - boxHeight / 2;
                view.Button.Width = boxWidth; view.Button.Height = boxHeight; view.Title.FontSize = narrow ? 10 : 12; view.Subtitle.FontSize = narrow ? 9 : 10;
                if (double.IsNaN(view.X) || !active || !SystemParameters.ClientAreaAnimation) { view.X = view.TargetX; view.Y = view.TargetY; Canvas.SetLeft(view.Button, view.X); Canvas.SetTop(view.Button, view.Y); }
            }
        }
        lines.InvalidateVisual();
    }
    private void SetHover(string? id)
    {
        hovered = id;
        foreach (var view in views.Values)
        {
            var related = id is null || view.Node.Id == id || model.Links.Any(link => (link.From == id && link.To == view.Node.Id) || (link.To == id && link.From == view.Node.Id));
            view.Button.Opacity = related ? 1 : .45;
            view.Button.BorderThickness = new Thickness(view.Node.Id == id ? 1.5 : 1);
            view.Button.SetResourceReference(Control.BorderBrushProperty, view.Node.Id == id ? "AccentBrush" : "BorderBrush");
        }
        lines.InvalidateVisual(); Wake();
    }
    private void Wake()
    {
        if (!active || !IsVisible || !SystemParameters.ClientAreaAnimation) { timer.Stop(); Place(); lines.InvalidateVisual(); return; }
        if (!timer.IsEnabled) { lastFrame = clock.Elapsed.TotalSeconds; timer.Start(); }
    }
    private void Tick()
    {
        if (!active || !IsVisible || !SystemParameters.ClientAreaAnimation) { timer.Stop(); return; }
        var now = clock.Elapsed.TotalSeconds; var ease = Math.Min(1, Math.Clamp(now - lastFrame, 0, .05) * 9); lastFrame = now;
        var moving = false;
        foreach (var view in views.Values)
        {
            view.X += (view.TargetX - view.X) * ease; view.Y += (view.TargetY - view.Y) * ease;
            if (Math.Abs(view.X - view.TargetX) <= .3) view.X = view.TargetX;
            if (Math.Abs(view.Y - view.TargetY) <= .3) view.Y = view.TargetY;
            moving |= view.X != view.TargetX || view.Y != view.TargetY;
            Canvas.SetLeft(view.Button, view.X); Canvas.SetTop(view.Button, view.Y);
        }
        lines.InvalidateVisual();
        if (!moving && !model.Links.Any(link => link.Rate > 0 && link.Kind != "reject")) timer.Stop();
    }

    private sealed class NodeView
    {
        public TopologyNode Node;
        public readonly Button Button = new() { Padding = new Thickness(8, 3, 8, 3), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), MinWidth = 0, MinHeight = 0 };
        public readonly TextBlock Title = Ui.Text("", 12, true);
        public readonly TextBlock Subtitle = Ui.Muted("", 10);
        public readonly Rectangle Marker = new() { Width = 7, Height = 7, RadiusX = 4, RadiusY = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        public readonly Border Flash = new() { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-8, -3, -8, -3) };
        public double X = double.NaN, Y, TargetX, TargetY;
        public NodeView(TopologyNode node)
        {
            Node = node; Button.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush"); Button.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            Title.TextWrapping = Subtitle.TextWrapping = TextWrapping.NoWrap; Title.TextTrimming = Subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            Subtitle.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.Children.Add(Marker); var copy = Ui.Column(Title, Subtitle); Grid.SetColumn(copy, 1); layout.Children.Add(copy); Grid.SetColumnSpan(Flash, 2); layout.Children.Add(Flash); Button.Content = layout;
        }
    }

    private sealed class LinkSurface(ProxyTopologyControl owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            foreach (var link in owner.model.Links)
            {
                if (!owner.views.TryGetValue(link.From, out var source) || !owner.views.TryGetValue(link.To, out var target) || double.IsNaN(source.X) || double.IsNaN(target.X)) continue;
                var p0 = new Point(source.X + owner.boxWidth, source.Y + owner.boxHeight / 2);
                var p3 = new Point(target.X, target.Y + owner.boxHeight / 2); var handle = Math.Max(20, (p3.X - p0.X) * .48);
                var p1 = new Point(p0.X + handle, p0.Y); var p2 = new Point(p3.X - handle, p3.Y);
                var geometry = new StreamGeometry(); using (var path = geometry.Open()) { path.BeginFigure(p0, false, false); path.BezierTo(p1, p2, p3, true, false); } geometry.Freeze();
                var weight = link.Rate > 0 ? Math.Min(1, Math.Log10(1 + link.Rate / 2048) / 3.2) : 0;
                var related = owner.hovered is null || link.From == owner.hovered || link.To == owner.hovered;
                var key = link.Kind == "direct" ? "TextSecondaryBrush" : link.Kind == "reject" ? "DangerBrush" : "AccentBrush";
                var brush = (Brush?)owner.TryFindResource(key) ?? Brushes.Gray;
                drawing.PushOpacity(related ? link.Rate > 0 ? .45 + weight * .4 : .25 : .08);
                var pen = new Pen(brush, 1 + weight * 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (link.Kind == "reject") pen.DashStyle = DashStyles.Dash;
                drawing.DrawGeometry(null, pen, geometry); drawing.Pop();
                if (!owner.active || !owner.IsVisible || !SystemParameters.ClientAreaAnimation || link.Rate <= 0 || link.Kind == "reject") continue;
                var count = Math.Clamp((int)Math.Ceiling(1 + weight * 7), 1, 8);
                for (var particle = 0; particle < count; particle++)
                {
                    var t = (owner.clock.Elapsed.TotalSeconds * (.32 + weight * .35) + (double)particle / count) % 1;
                    var u = 1 - t;
                    var point = new Point(u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X, u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
                    var radius = 1.1 + weight * 1.5;
                    drawing.PushOpacity(related ? .16 : .04); drawing.DrawEllipse(brush, null, point, radius * 2.5, radius * 2.5); drawing.Pop();
                    drawing.PushOpacity(related ? .9 : .15); drawing.DrawEllipse(brush, null, point, radius, radius); drawing.Pop();
                }
            }
        }
    }
}
