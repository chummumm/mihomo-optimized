using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed record ConnectionDetailSnapshot(string Id, bool Closed, bool Dns, string Network, string Source, string Inbound, string Rule, string Chain, RouteHop[] Hops, string Host, string Destination, string Started, string Process, string ProcessPath, string Upload, string Download);

/// <summary>Native counterpart of the uploaded in-table 440-pixel connection journey drawer.</summary>
internal sealed class ConnectionDrawer : Border
{
    private readonly TextBlock state = Ui.Text("", 12);
    private readonly Ellipse lamp = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border stateBadge;
    private readonly TextBlock network = Ui.Muted("", 12);
    private readonly StackPanel journey = new();
    private readonly Grid facts = new();
    private readonly Dictionary<string, TextBlock> values = new(StringComparer.Ordinal);
    private readonly ScrollViewer scroll;
    private readonly Button closeConnection;
    private readonly TranslateTransform slide = new();
    private string layoutStamp = "";
    private ConnectionDetailSnapshot? snapshot;
    private bool mobile, busy, lampAnimating;
    private long transition;
    internal bool IsOpen { get; private set; }
    internal event Action? Closed;
    internal event Action? Closing;
    internal event Func<Task>? Disconnect;
    internal ConnectionDrawer()
    {
        this.Id("ConnectionDetails"); L.Bind(this, AutomationProperties.NameProperty, "连接信息");
        Width = 440; HorizontalAlignment = HorizontalAlignment.Right; Margin = new Thickness(10); CornerRadius = new CornerRadius(8); BorderThickness = new Thickness(1); Visibility = Visibility.Collapsed; RenderTransform = slide;
        SetResourceReference(BorderBrushProperty, "AcrylicStrokeBrush"); SetResourceReference(BackgroundProperty, "DialogBottomBrush");
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Child = layout;
        var header = new DockPanel { Margin = new Thickness(18, 0, 8, 0) }; var close = FluentControls.IconButton("close", "关闭详情", "CloseConnectionDetails"); close.Click += (_, _) => Hide(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(Ui.Label("连接信息", 14, true));
        var headerFrame = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }; headerFrame.SetResourceReference(BorderBrushProperty, "DividerBrush"); layout.Children.Add(headerFrame);
        var statusCopy = new StackPanel { Orientation = Orientation.Horizontal }; statusCopy.Children.Add(lamp); statusCopy.Children.Add(state);
        stateBadge = new Border { Child = statusCopy, Height = 24, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(4) }; network.FontFamily = new FontFamily("Cascadia Mono, Consolas"); network.Margin = new Thickness(8, 0, 0, 0);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) }; status.Children.Add(stateBadge); status.Children.Add(network);
        journey.Margin = new Thickness(0, 0, 0, 16);
        var factsFrame = new Border { Child = facts, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0) }; factsFrame.SetResourceReference(BorderBrushProperty, "DividerBrush");
        scroll = new ScrollViewer { Content = Ui.Column(status, journey, factsFrame), Padding = new Thickness(18, 16, 18, 16), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        closeConnection = new Button { Content = FluentControls.ButtonContent("prohibit", "关闭连接"), Height = 32, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 0, 12, 0), Style = FluentControls.Style("Button") }.Id("CloseConnection"); L.Bind(closeConnection, AutomationProperties.NameProperty, "关闭连接"); FluentControlState.SetKind(closeConnection, "Danger");
        closeConnection.Click += async (_, _) => { if (busy || snapshot is not { Closed: false }) return; busy = true; closeConnection.IsEnabled = false; try { if (Disconnect is { } disconnect) await disconnect(); } finally { busy = false; closeConnection.IsEnabled = snapshot is { Closed: false }; } };
        var footer = new Border { Child = closeConnection, Padding = new Thickness(18, 12, 18, 12), BorderThickness = new Thickness(0, 1, 0, 0) }; footer.SetResourceReference(BackgroundProperty, "DialogTopBrush"); footer.SetResourceReference(BorderBrushProperty, "DividerBrush"); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Loaded += (_, _) => { DesignTokens.Changed += Palette; L.Changed += Relocalize; Palette(); UpdateLamp(); }; Unloaded += (_, _) => { DesignTokens.Changed -= Palette; L.Changed -= Relocalize; lamp.BeginAnimation(OpacityProperty, null); lampAnimating = false; };
        IsVisibleChanged += (_, _) => UpdateLamp();
    }
    private void Relocalize() { layoutStamp = ""; if (snapshot is { } current) Update(current); }
    private void Palette()
    {
        var dark = TryFindResource("SolidBaseBrush") is SolidColorBrush background && background.Color.R < 128;
        Effect = SystemParameters.HighContrast ? null : new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 8, Opacity = dark ? .26 : .14 };
    }
    internal void SetMobile(bool value)
    {
        mobile = value; Width = value ? double.NaN : 440; HorizontalAlignment = value ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Margin = value ? new Thickness(0) : new Thickness(10); scroll.Padding = value ? new Thickness(16, 12, 16, 12) : new Thickness(18, 16, 18, 16);
        if (IsOpen) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.BeginAnimation(TranslateTransform.YProperty, null); slide.X = slide.Y = 0; }
    }
    internal void Show(ConnectionDetailSnapshot value)
    {
        var wasOpen = IsOpen; IsOpen = true; ++transition; Visibility = Visibility.Visible; IsHitTestVisible = true; Update(value);
        if (wasOpen) return;
        UpdateLayout(); var distance = mobile ? Math.Max(ActualHeight, 300) * 1.1 : Math.Max(ActualWidth, 440) * 1.1;
        ProxyVisuals.Tween(slide, mobile ? TranslateTransform.YProperty : TranslateTransform.XProperty, distance, 0, 250);
    }
    internal void Hide(bool animate = true)
    {
        if (!IsOpen) return; Closing?.Invoke(); IsOpen = false; IsHitTestVisible = false; var generation = ++transition;
        void Complete() { if (generation != transition) return; Visibility = Visibility.Collapsed; UpdateLamp(); Closed?.Invoke(); }
        if (!animate || !ProxyVisuals.Animate) { Complete(); return; }
        ProxyVisuals.Tween(slide, mobile ? TranslateTransform.YProperty : TranslateTransform.XProperty, mobile ? slide.Y : slide.X, (mobile ? Math.Max(ActualHeight, 300) : Math.Max(ActualWidth, 440)) * 1.1, 250, complete: Complete);
    }
    internal void Update(ConnectionDetailSnapshot value)
    {
        snapshot = value;
        state.Text = L.T(value.Closed ? "已关闭" : "连接中"); network.Text = value.Network; closeConnection.IsEnabled = !value.Closed && !busy;
        stateBadge.SetResourceReference(BackgroundProperty, value.Closed ? "ControlAltBrush" : "SuccessBackgroundBrush"); state.SetResourceReference(TextBlock.ForegroundProperty, value.Closed ? "TextSecondaryBrush" : "TextPrimaryBrush"); lamp.SetResourceReference(Shape.FillProperty, value.Closed ? "TextSecondaryBrush" : "SuccessBrush");
        UpdateLamp();
        var stamp = string.Join('\n', value.Id, value.Dns.ToString(), value.Source, value.Inbound, value.Rule, value.Chain, value.Host, value.Destination, value.Started, value.Process, value.ProcessPath);
        if (layoutStamp != stamp)
        {
            layoutStamp = stamp; journey.Children.Clear(); facts.Children.Clear(); facts.RowDefinitions.Clear(); facts.ColumnDefinitions.Clear(); values.Clear();
            var stages = new List<(string Label, string Value, string Kind)> { ("来源", value.Source, "origin"), ("入口", value.Inbound, "normal") };
            if (!value.Dns) stages.Add(("规则", value.Rule, "normal")); stages.Add(("代理", value.Chain, "routed")); stages.Add(("域名", value.Host, "end"));
            for (var index = 0; index < stages.Count; index++) { var stage = stages[index]; journey.Children.Add(new JourneyStop(stage.Label, stage.Value, stage.Kind, value.Hops.FirstOrDefault()?.Brush ?? "TextTertiaryBrush", index, index < stages.Count - 1)); }
            AddFact("destination", "IP", value.Destination); AddPair("上传", value.Upload, "下载", value.Download); AddFact("start", "开始时间", value.Started); AddFact("process", "进程", value.Process); AddFact("path", "路径", value.ProcessPath); AddFact("id", "ID", value.Id);
        }
        if (values.TryGetValue("upload", out var upload)) upload.Text = value.Upload; if (values.TryGetValue("download", out var download)) download.Text = value.Download;
    }
    private void UpdateLamp()
    {
        var animate = IsLoaded && IsVisible && IsOpen && snapshot is { Closed: false } && ProxyVisuals.Animate;
        if (lampAnimating == animate) return;
        lampAnimating = animate; lamp.BeginAnimation(OpacityProperty, null); lamp.Opacity = 1;
        if (!animate) return;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600), RepeatBehavior = RepeatBehavior.Forever };
        pulse.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new SplineDoubleKeyFrame(.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800)), new KeySpline(.42, 0, .58, 1)));
        pulse.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600)), new KeySpline(.42, 0, .58, 1))); lamp.BeginAnimation(OpacityProperty, pulse);
    }
    private void AddFact(string key, string title, string value)
    {
        var block = Fact(key, title, value); var row = facts.RowDefinitions.Count; facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(block, row); if (row > 0) block.Margin = new Thickness(0, 12, 0, 0); facts.Children.Add(block);
    }
    private StackPanel Fact(string key, string title, string text)
    {
        var caption = Ui.Hint(title, 12); var value = Ui.Text(string.IsNullOrEmpty(text) ? L.T("空") : text, 12); value.FontFamily = new FontFamily("Cascadia Mono, Consolas"); value.Margin = new Thickness(0, 3, 0, 0); value.TextWrapping = TextWrapping.Wrap; values[key] = value; return Ui.Column(caption, value);
    }
    private void AddPair(string leftTitle, string leftValue, string rightTitle, string rightValue)
    {
        var pair = new Grid { Margin = new Thickness(0, 12, 0, 0) }; pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition()); var left = Fact("upload", leftTitle, leftValue); left.Margin = new Thickness(0, 0, 7, 0); pair.Children.Add(left); var right = Fact("download", rightTitle, rightValue); right.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(right, 1); pair.Children.Add(right);
        var row = facts.RowDefinitions.Count; facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(pair, row); facts.Children.Add(pair);
    }
    private sealed class JourneyStop : Grid
    {
        private readonly bool connector, routed;
        private readonly int index;
        private readonly Ellipse particle = new() { Width = 5, Height = 5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4.5, 24, 0, 0), IsHitTestVisible = false };
        private readonly TranslateTransform movement = new();
        internal JourneyStop(string label, string value, string kind, string line, int index, bool connector)
        {
            this.connector = connector; routed = kind == "routed"; this.index = index; ClipToBounds = false;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) }); ColumnDefinitions.Add(new ColumnDefinition());
            var mark = new Ellipse { Width = 14, Height = 14, StrokeThickness = routed ? 2.5 : 1.5, Margin = new Thickness(0, 11, 12, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            mark.SetResourceReference(Shape.StrokeProperty, kind == "end" ? "AccentBrush" : routed ? line : kind == "origin" ? "TextSecondaryBrush" : "TextTertiaryBrush"); mark.SetResourceReference(Shape.FillProperty, kind == "end" ? "AccentBrush" : "DialogBottomBrush"); Children.Add(mark);
            var caption = Ui.Hint(label, 12); caption.Margin = new Thickness(0, 8, 12, 8); caption.VerticalAlignment = VerticalAlignment.Top; caption.LineHeight = 20; caption.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; Grid.SetColumn(caption, 1); Children.Add(caption);
            var text = Ui.Text(string.IsNullOrEmpty(value) ? L.T("空") : value, 12, kind == "end"); text.FontFamily = new FontFamily("Cascadia Mono, Consolas"); text.LineHeight = 20; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; text.Margin = new Thickness(0, 8, 0, 8); text.VerticalAlignment = VerticalAlignment.Top; if (routed) text.SetResourceReference(TextBlock.ForegroundProperty, "AccentLinkBrush"); Grid.SetColumn(text, 2); Children.Add(text);
            if (connector) { particle.SetResourceReference(Shape.FillProperty, "AccentBrush"); particle.RenderTransform = movement; Children.Add(particle); }
            Loaded += (_, _) => Animate(); IsVisibleChanged += (_, _) => Animate(); Unloaded += (_, _) => { movement.BeginAnimation(TranslateTransform.YProperty, null); particle.BeginAnimation(OpacityProperty, null); };
        }
        private void Animate()
        {
            movement.BeginAnimation(TranslateTransform.YProperty, null); particle.BeginAnimation(OpacityProperty, null); particle.Opacity = 0;
            if (!connector || !IsVisible || !ProxyVisuals.Animate) return;
            var offset = TimeSpan.FromMilliseconds(index * 240); var duration = TimeSpan.FromMilliseconds(1400);
            movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 18, duration) { BeginTime = offset, RepeatBehavior = RepeatBehavior.Forever }); particle.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration) { BeginTime = offset, RepeatBehavior = RepeatBehavior.Forever });
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing); if (!connector) return;
            var brush = TryFindResource(routed ? "AccentBrush" : "DividerBrush") as Brush ?? Brushes.Gray; if (routed) { brush = brush.CloneCurrentValue(); brush.Opacity *= .5; }
            drawing.DrawLine(new Pen(brush, routed ? 2 : 1), new Point(7, 24), new Point(7, ActualHeight + 8));
        }
    }
}

/// <summary>The narrow-screen drawer rises over the complete client viewport.</summary>
internal sealed class ConnectionDrawerSheet : Adorner
{
    private readonly VisualCollection visuals;
    private readonly Grid layer = new();
    private readonly Border smoke = new() { IsHitTestVisible = false };
    private readonly ConnectionDrawer drawer;
    internal ConnectionDrawerSheet(FrameworkElement viewport, ConnectionDrawer drawer) : base(viewport)
    {
        this.drawer = drawer; visuals = new VisualCollection(this) { layer }; smoke.SetResourceReference(Border.BackgroundProperty, "ScrimBrush"); layer.Children.Add(smoke); layer.Children.Add(drawer); drawer.Margin = new Thickness(8); drawer.VerticalAlignment = VerticalAlignment.Bottom;
        smoke.Id("ConnectionDrawerSmoke");
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { drawer.Hide(); e.Handled = true; } };
        ProxyVisuals.Tween(smoke, OpacityProperty, 0, 1, 250);
    }
    internal void HideSmoke() => ProxyVisuals.Tween(smoke, OpacityProperty, smoke.Opacity, 0, 250);
    internal void Release() { layer.Children.Remove(drawer); drawer.Margin = new Thickness(10); drawer.VerticalAlignment = VerticalAlignment.Stretch; drawer.Height = double.NaN; }
    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];
    protected override Size MeasureOverride(Size constraint) { drawer.Height = Math.Min(640, Math.Max(0, AdornedElement.RenderSize.Height * .76)); layer.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
    protected override Size ArrangeOverride(Size finalSize) { layer.Arrange(new Rect(finalSize)); return finalSize; }
}
