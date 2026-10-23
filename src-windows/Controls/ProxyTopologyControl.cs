using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The uploaded canvas drawing, rendered with WPF paths and native, focusable node buttons.</summary>
internal sealed class ProxyTopologyControl : Grid
{
    private readonly Canvas boxes = new();
    private readonly LinkSurface lines;
    private readonly Dictionary<string, NodeView> views = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), LinkView> links = [];
    private readonly List<Particle> particles = [];
    private readonly TextBlock[] headings = [Ui.Label("来源", 10), Ui.Label("策略组", 10), Ui.Label("出口", 10)];
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 60) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TopologyModel model = TopologyModel.Empty;
    private bool active, compact;
    private string? hovered;
    private double lastFrame;
    private double boxWidth = 180, boxHeight = 42;
    public event Action<string, string>? Picked;
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    public ProxyTopologyControl()
    {
        this.Id("ProxyTopology"); ClipToBounds = false;
        lines = new LinkSurface(this); Children.Add(lines); Children.Add(boxes);
        foreach (var heading in headings) { heading.IsHitTestVisible = false; heading.FontFamily = new FontFamily("Cascadia Mono, Consolas"); heading.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush"); boxes.Children.Add(heading); }
        SizeChanged += (_, _) => { Place(); Wake(); }; IsVisibleChanged += (_, _) => Wake(); timer.Tick += (_, _) => Tick();
        L.Changed += () => { UpdateLabels(); lines.InvalidateVisual(); };
    }
    internal void SetCompact(bool value) { if (compact == value) return; compact = value; Place(); Wake(); }
    public void SetActive(bool value)
    {
        active = value;
        if (!value)
        {
            timer.Stop(); hovered = null; particles.Clear();
            foreach (var view in views.Values) { view.X = view.TargetX; view.Y = view.TargetY; view.Alpha = view.TargetAlpha; PlaceButton(view); }
            foreach (var link in links.Values) link.Alpha = link.Goal;
            Prune(); ApplyHover();
        }
        Wake();
    }
    public void SetModel(TopologyModel value)
    {
        model = value; var desired = value.Nodes.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in views) if (!desired.Contains(pair.Key)) { pair.Value.TargetAlpha = 0; pair.Value.Button.IsHitTestVisible = false; }
        foreach (var node in value.Nodes)
        {
            if (!views.TryGetValue(node.Id, out var view))
            {
                view = new NodeView(node); views[node.Id] = view; boxes.Children.Add(view.Button); var id = node.Id;
                view.Button.MouseEnter += (_, _) => SetHover(id); view.Button.MouseLeave += (_, _) => SetHover(null); view.Button.GotKeyboardFocus += (_, _) => SetHover(id); view.Button.LostKeyboardFocus += (_, _) => SetHover(null);
                view.Button.Click += (_, _) => { var current = views.GetValueOrDefault(id)?.Node; if (current?.Target is { } target) Picked?.Invoke(current.Kind == "group" ? "focus-group" : "focus-node", target); };
            }
            var before = view.Node.Stamp; view.Node = node; view.TargetAlpha = 1; view.Button.IsHitTestVisible = true;
            view.Title.Text = node.Title; view.Subtitle.Text = node.Subtitle; view.TipTitle.Text = node.Title; view.TipSubtitle.Text = node.Subtitle;
            AutomationProperties.SetName(view.Button, node.Title + " · " + node.Subtitle); view.Button.Focusable = node.Target is not null; view.Button.Cursor = node.Target is not null ? Cursors.Hand : Cursors.Arrow;
            view.Button.Id(node.Kind == "group" ? "TopologyGroup_" + node.Title : node.Kind == "exit" ? "TopologyNode_" + node.Title : "TopologySource_" + node.Title);
            var mark = node.Kind == "group" && node.GroupColor.HasValue ? "Line" + node.GroupColor.Value + "Brush" : ProxyVisuals.Tone(node.Tone);
            view.Marker.SetResourceReference(Shape.FillProperty, mark); view.Marker.RadiusX = view.Marker.RadiusY = node.Kind == "group" ? 2 : 4;
            view.Ping.SetResourceReference(Shape.StrokeProperty, mark);
            if (before is not null && node.Stamp != before && node.Kind != "group" && ProxyVisuals.Animate)
            {
                var duration = TimeSpan.FromMilliseconds(800); var centerX = ActualWidth < 620 ? 10 : 13; var centerY = boxHeight / 2;
                view.Ping.BeginAnimation(WidthProperty, new DoubleAnimation(8, 36, duration)); view.Ping.BeginAnimation(HeightProperty, new DoubleAnimation(8, 36, duration));
                view.Ping.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(centerX - 4, centerX - 18, duration)); view.Ping.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(centerY - 4, centerY - 18, duration));
                view.Ping.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
            }
        }
        var wantedLinks = value.Links.Select(link => (link.From, link.To)).ToHashSet();
        foreach (var item in links) if (!wantedLinks.Contains(item.Key)) item.Value.Goal = 0;
        foreach (var link in value.Links)
        {
            if (!links.TryGetValue((link.From, link.To), out var view)) links[(link.From, link.To)] = view = new LinkView(link);
            view.Link = link; view.Goal = 1;
        }
        Place(); UpdateLabels(); if (!active || !ProxyVisuals.Animate) { foreach (var view in views.Values) view.Alpha = view.TargetAlpha; foreach (var link in links.Values) link.Alpha = link.Goal; Prune(); }
        ApplyHover(); Wake();
    }
    public void RefreshPalette() { ApplyHover(); lines.InvalidateVisual(); Wake(); }
    private void UpdateLabels() => AutomationProperties.SetName(this, L.F("实时流量拓扑，{0} 个连接", model.Connections));
    private void Place()
    {
        if (ActualWidth <= 0) return;
        var narrow = ActualWidth < 620; boxWidth = narrow ? Math.Max(78, (ActualWidth - 76) / 3) : Math.Min(200, (ActualWidth - 4) / 4.3); boxHeight = narrow ? 36 : 42;
        var xs = new[] { 2d, (ActualWidth - boxWidth) / 2, ActualWidth - 2 - boxWidth }; Height = (compact ? 30 : 32) + Math.Max(3, model.Rows) * (compact ? 46 : 54);
        for (var column = 0; column < 3; column++)
        {
            headings[column].Measure(new Size(boxWidth, double.PositiveInfinity));
            Canvas.SetLeft(headings[column], xs[column] + 2); Canvas.SetTop(headings[column], 10 - headings[column].DesiredSize.Height / 2); headings[column].MaxWidth = boxWidth;
            var members = model.Nodes.Where(item => item.Column == column).ToArray();
            for (var index = 0; index < members.Length; index++)
            {
                var view = views[members[index].Id]; view.TargetX = xs[column]; view.TargetY = 26 + (Height - 30) * (index + .5) / members.Length - boxHeight / 2;
                view.Button.Width = boxWidth; view.Button.Height = boxHeight; view.Title.FontSize = narrow ? 10 : 12; view.Subtitle.FontSize = narrow ? 9 : 10;
                view.Title.Width = view.Subtitle.Width = Math.Max(0, boxWidth - (narrow ? 25 : 33));
                Canvas.SetLeft(view.Title, narrow ? 20 : 25); Canvas.SetTop(view.Title, boxHeight / 2 - (narrow ? 1 : 2) - view.Title.FontFamily.Baseline * view.Title.FontSize);
                Canvas.SetLeft(view.Subtitle, narrow ? 20 : 25); Canvas.SetTop(view.Subtitle, boxHeight / 2 + (narrow ? 11 : 13) - view.Subtitle.FontFamily.Baseline * view.Subtitle.FontSize);
                var size = view.Node.Kind == "group" ? 7 : view.Node.Kind == "more" ? 4.8 : 6.4; view.Marker.Width = view.Marker.Height = size;
                Canvas.SetLeft(view.Marker, (narrow ? 10 : 13) - size / 2); Canvas.SetTop(view.Marker, boxHeight / 2 - size / 2);
                Canvas.SetLeft(view.Ping, (narrow ? 10 : 13) - 4); Canvas.SetTop(view.Ping, boxHeight / 2 - 4);
                if (double.IsNaN(view.X) || !active || !ProxyVisuals.Animate) { view.X = view.TargetX; view.Y = view.TargetY; PlaceButton(view); }
            }
        }
        lines.InvalidateVisual();
    }
    private void PlaceButton(NodeView view)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; Canvas.SetLeft(view.Button, Math.Round(view.X * dpi) / dpi); Canvas.SetTop(view.Button, Math.Round(view.Y * dpi) / dpi);
    }
    private bool Related(string id) => hovered is null || id == hovered || model.Links.Any(link => (link.From == hovered && link.To == id) || (link.To == hovered && link.From == id));
    private void SetHover(string? id) { hovered = id; ApplyHover(); lines.InvalidateVisual(); Wake(); }
    private bool Dark => TryFindResource("PanelSolidBrush") is SolidColorBrush brush && (.2126 * brush.Color.R + .7152 * brush.Color.G + .0722 * brush.Color.B) / 255 < .45;
    private void ApplyHover()
    {
        foreach (var view in views.Values)
        {
            var hot = view.Node.Id == hovered; view.Button.Opacity = view.Alpha * (Related(view.Node.Id) ? 1 : .45);
            view.Button.SetResourceReference(Control.BorderBrushProperty, hot ? "AccentBrush" : "HairStrongBrush");
            view.Button.Effect = hot && Dark && !SystemParameters.HighContrast && TryFindResource("AccentBrush") is SolidColorBrush accent ? new DropShadowEffect { Color = accent.Color, BlurRadius = 16, ShadowDepth = 0, Opacity = .65 } : null;
            view.Marker.Effect = Dark && view.Node.Kind != "group" && !SystemParameters.HighContrast && view.Marker.Fill is SolidColorBrush color ? new DropShadowEffect { Color = color.Color, BlurRadius = 8, ShadowDepth = 0, Opacity = .7 } : null;
        }
    }
    private void Wake()
    {
        if (!active || !IsVisible || !ProxyVisuals.Animate) { timer.Stop(); Place(); lines.InvalidateVisual(); return; }
        if (!timer.IsEnabled) { lastFrame = clock.Elapsed.TotalSeconds; timer.Start(); }
    }
    private void Tick()
    {
        if (!active || !IsVisible || !ProxyVisuals.Animate) { timer.Stop(); return; }
        var now = clock.Elapsed.TotalSeconds; var dt = Math.Clamp(now - lastFrame, 0, .05); var ease = Math.Min(1, dt * 9); lastFrame = now; var moving = false;
        foreach (var view in views.Values)
        {
            view.X += (view.TargetX - view.X) * ease; view.Y += (view.TargetY - view.Y) * ease; view.Alpha += (view.TargetAlpha - view.Alpha) * Math.Min(1, dt * 6);
            if (Math.Abs(view.X - view.TargetX) <= .3) view.X = view.TargetX; if (Math.Abs(view.Y - view.TargetY) <= .3) view.Y = view.TargetY; if (Math.Abs(view.Alpha - view.TargetAlpha) <= .01) view.Alpha = view.TargetAlpha;
            moving |= view.X != view.TargetX || view.Y != view.TargetY || view.Alpha != view.TargetAlpha; PlaceButton(view); view.Button.Opacity = view.Alpha * (Related(view.Node.Id) ? 1 : .45);
        }
        foreach (var link in links.Values)
        {
            link.Alpha += (link.Goal - link.Alpha) * Math.Min(1, dt * 4); if (Math.Abs(link.Goal - link.Alpha) <= .01) link.Alpha = link.Goal; moving |= link.Alpha != link.Goal;
            if (link.Link.Rate <= 0 || link.Alpha <= .02 || link.Link.Kind == "reject") continue;
            var weight = Weight(link.Link.Rate); link.Spawn += dt * (.5 + weight * 7.5); moving = true;
            while (link.Spawn > 1) { link.Spawn--; particles.Add(new Particle(link, .32 + Random.Shared.NextDouble() * .22 + weight * .35, 1.1 + weight * 1.5)); }
        }
        foreach (var particle in particles) particle.T += dt * particle.Speed;
        particles.RemoveAll(item => item.T >= 1 || item.Link.Alpha < .02); if (particles.Count > 800) particles.RemoveRange(0, particles.Count - 800);
        moving |= particles.Count > 0; Prune(); lines.InvalidateVisual(); if (!moving) timer.Stop();
    }
    private void Prune()
    {
        foreach (var id in views.Keys.Where(id => views[id].TargetAlpha == 0 && views[id].Alpha < .02).ToArray()) { boxes.Children.Remove(views[id].Button); views.Remove(id); }
        foreach (var id in links.Keys.Where(id => links[id].Goal == 0 && links[id].Alpha < .02).ToArray()) links.Remove(id);
    }
    private static double Weight(double rate) => rate > 0 ? Math.Min(1, Math.Log10(1 + rate / 2048) / 3.2) : 0;
    private sealed class LinkView(TopologyLink link) { public TopologyLink Link = link; public double Alpha, Goal = 1, Spawn = Random.Shared.NextDouble(); }
    private sealed class Particle(LinkView link, double speed, double radius) { public readonly LinkView Link = link; public readonly double Speed = speed, Radius = radius; public double T; }
    private sealed class NodeView
    {
        public TopologyNode Node;
        public readonly Button Button = new() { Padding = new Thickness(0), Style = ProxyVisuals.Style("Graph"), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, BorderThickness = new Thickness(1), MinWidth = 0, MinHeight = 0 };
        public readonly TextBlock Title = Ui.Text("", 12, true), Subtitle = Ui.Muted("", 10), TipTitle = Ui.Text("", 12), TipSubtitle = Ui.Muted("", 12);
        public readonly Rectangle Marker = new() { Width = 7, Height = 7, RadiusX = 4, RadiusY = 4 };
        public readonly Ellipse Ping = new() { Width = 8, Height = 8, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5) };
        public double X = double.NaN, Y, TargetX, TargetY, Alpha, TargetAlpha = 1;
        public NodeView(TopologyNode node)
        {
            Node = node; Button.SetResourceReference(Control.BackgroundProperty, "PanelSolidBrush"); Button.SetResourceReference(Control.BorderBrushProperty, "HairStrongBrush");
            Title.TextWrapping = Subtitle.TextWrapping = TextWrapping.NoWrap; Title.TextTrimming = Subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            Subtitle.FontFamily = new FontFamily("Cascadia Mono, Consolas"); Subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            var canvas = new Canvas { Margin = new Thickness(-1) }; canvas.Children.Add(Ping); canvas.Children.Add(Marker); canvas.Children.Add(Title); canvas.Children.Add(Subtitle); Button.Content = canvas;
            TipTitle.LineHeight = TipSubtitle.LineHeight = 16; var tip = new ToolTip { Content = Ui.Column(TipTitle, TipSubtitle), MaxWidth = 280, Padding = new Thickness(8, 6, 8, 8), Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 6 }; Button.ToolTip = tip; ToolTipService.SetInitialShowDelay(Button, 0); ToolTipService.SetBetweenShowDelay(Button, 0);
        }
    }
    private sealed class LinkSurface(ProxyTopologyControl owner) : FrameworkElement
    {
        private (Point Start, Point End, double Middle)? Curve(TopologyLink link)
        {
            if (!owner.views.TryGetValue(link.From, out var from) || !owner.views.TryGetValue(link.To, out var to) || double.IsNaN(from.X) || double.IsNaN(to.X)) return null;
            var start = new Point(Canvas.GetLeft(from.Button) + owner.boxWidth, Canvas.GetTop(from.Button) + owner.boxHeight / 2); var end = new Point(Canvas.GetLeft(to.Button), Canvas.GetTop(to.Button) + owner.boxHeight / 2); return (start, end, (start.X + end.X) / 2);
        }
        private Brush BrushFor(TopologyLink link) => (Brush?)owner.TryFindResource(link.Kind == "direct" ? "FaintBrush" : link.Kind == "reject" ? "DangerBrush" : "AccentBrush") ?? Brushes.Gray;
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            foreach (var state in owner.links.Values)
            {
                var link = state.Link; if (Curve(link) is not { } curve) continue;
                var path = new StreamGeometry(); using (var writer = path.Open()) { writer.BeginFigure(curve.Start, false, false); writer.BezierTo(new Point(curve.Middle, curve.Start.Y), new Point(curve.Middle, curve.End.Y), curve.End, true, false); } path.Freeze();
                var weight = Weight(link.Rate); var related = owner.hovered is null || link.From == owner.hovered || link.To == owner.hovered;
                drawing.PushOpacity(state.Alpha * (related ? 1 : .12) * (.32 + weight * .58));
                var pen = new Pen(BrushFor(link), 1 + weight * 3.4); if (link.Rate <= 0) pen.DashStyle = new DashStyle([3 / pen.Thickness, 4 / pen.Thickness], 0);
                drawing.DrawGeometry(null, pen, path); drawing.Pop();
            }
            if (!owner.active || !owner.IsVisible || !ProxyVisuals.Animate) return;
            foreach (var particle in owner.particles)
            {
                var link = particle.Link.Link; if (Curve(link) is not { } curve) continue;
                var t = particle.T; var u = 1 - t; var point = new Point(u * u * u * curve.Start.X + 3 * u * u * t * curve.Middle + 3 * u * t * t * curve.Middle + t * t * t * curve.End.X, u * u * u * curve.Start.Y + 3 * u * u * t * curve.Start.Y + 3 * u * t * t * curve.End.Y + t * t * t * curve.End.Y);
                var related = owner.hovered is null || link.From == owner.hovered || link.To == owner.hovered; var alpha = particle.Link.Alpha * (related ? 1 : .1) * Math.Sin(Math.PI * t); var brush = BrushFor(link); var ratio = owner.ActualWidth < 620 ? 2.4 : 3.4;
                drawing.PushOpacity(alpha * (owner.Dark ? .32 : .16)); drawing.DrawEllipse(brush, null, point, particle.Radius * ratio, particle.Radius * ratio); drawing.Pop();
                drawing.PushOpacity(alpha); drawing.DrawEllipse(brush, null, point, particle.Radius, particle.Radius); drawing.Pop();
            }
        }
    }
}
