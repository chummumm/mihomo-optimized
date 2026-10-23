using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed record ConnectionSource(string Key, string Label, int Count, double Rate);

/// <summary>Original source rows/pills, a travelling selection, and measured traffic-share rings.</summary>
internal sealed class ConnectionSources : UserControl
{
    private readonly SourcePanel panel = new();
    private readonly Canvas highlightLayer = new() { IsHitTestVisible = false };
    private readonly Canvas retiring = new() { IsHitTestVisible = false };
    private readonly Border highlight = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly Border indicator = new() { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-1, 0, 0, 0) };
    private readonly TranslateTransform selectionPosition = new();
    private readonly ScrollViewer scroll;
    private readonly Dictionary<string, SourceButton> buttons = new(StringComparer.Ordinal);
    private string selected = "";
    private bool applying, positioned, wide;
    private Rect lastSelection;
    public event Action<string>? Selected;
    internal ConnectionSources()
    {
        this.Id("ConnectionSources"); L.Bind(this, AutomationProperties.NameProperty, "连接来源");
        highlight.RenderTransform = selectionPosition; highlight.Child = indicator; indicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); highlightLayer.Children.Add(highlight);
        var surface = new Grid(); surface.Children.Add(highlightLayer); surface.Children.Add(panel); surface.Children.Add(retiring);
        scroll = new ScrollViewer { Content = surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0) }; Content = scroll;
        panel.Arranged += PositionSelection; Loaded += (_, _) => { DesignTokens.Changed += SetPalette; SetPalette(); }; Unloaded += (_, _) => DesignTokens.Changed -= SetPalette;
    }
    internal void SetWide(bool value, double windowHeight, bool phone = false)
    {
        wide = value; panel.Wide = value; panel.NoWrap = phone; panel.InvalidateMeasure();
        scroll.MaxHeight = value ? double.PositiveInfinity : Math.Max(32, windowHeight * .25);
        scroll.HorizontalScrollBarVisibility = phone ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        scroll.VerticalScrollBarVisibility = phone ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        scroll.Margin = value ? new Thickness(-8, 0, -8, 0) : new Thickness(0); panel.Padding = value ? new Thickness(8, 0, 8, 2) : new Thickness(0);
        foreach (var button in buttons.Values) button.SetWide(value);
        SetPalette(); PositionSelection();
    }
    private void SetPalette()
    {
        indicator.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        if (wide) { highlight.BorderBrush = Brushes.Transparent; highlight.SetResourceReference(Border.BackgroundProperty, "SubtleHoverBrush"); }
        else
        {
            highlight.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            if (TryFindResource("AccentBrush") is SolidColorBrush accent && TryFindResource("SolidCardBrush") is SolidColorBrush card)
            {
                byte Mix(byte foreground, byte background) => (byte)Math.Round(foreground * .1 + background * .9);
                highlight.Background = new SolidColorBrush(Color.FromRgb(Mix(accent.Color.R, card.Color.R), Mix(accent.Color.G, card.Color.G), Mix(accent.Color.B, card.Color.B)));
            }
        }
        panel.InvalidateVisual();
    }
    internal void Update(IReadOnlyList<ConnectionSource> sources, string current)
    {
        selected = current; var wanted = sources.Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in buttons.Keys.Where(key => !wanted.Contains(key)).ToArray())
        {
            var old = buttons[key]; var bounds = LayoutInformation.GetLayoutSlot(old); panel.Children.Remove(old); buttons.Remove(key);
            if (!IsVisible || !ProxyVisuals.Animate || old.ActualWidth <= 0) continue;
            old.Retire(); old.Width = bounds.Width; old.Height = bounds.Height; Canvas.SetLeft(old, bounds.X); Canvas.SetTop(old, bounds.Y); retiring.Children.Add(old);
            var scale = new ScaleTransform(1, 1); old.RenderTransform = scale; old.RenderTransformOrigin = new Point(.5, .5);
            ProxyVisuals.Tween(scale, ScaleTransform.ScaleXProperty, 1, .9, 250); ProxyVisuals.Tween(scale, ScaleTransform.ScaleYProperty, 1, .9, 250); ProxyVisuals.Tween(old, OpacityProperty, old.Opacity, 0, 250, () => retiring.Children.Remove(old));
        }
        var total = sources.FirstOrDefault(source => source.Key.Length == 0)?.Rate ?? 0;
        applying = true;
        try
        {
            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                if (!buttons.TryGetValue(source.Key, out var button))
                {
                    button = new SourceButton().Id("ConnectionSource_" + (source.Key.Length == 0 ? "all" : source.Key)); button.SetWide(wide); buttons[source.Key] = button; var key = source.Key;
                    button.Checked += (_, _) => { if (!applying) Selected?.Invoke(key); };
                    button.Unchecked += (_, _) => { if (!applying && selected == key) { applying = true; button.IsChecked = true; applying = false; } };
                    if (IsLoaded && source.Key.Length > 0 && ProxyVisuals.Animate) button.Enter();
                }
                button.Set(source, total, current == source.Key);
                var previous = panel.Children.IndexOf(button); if (previous != index) { if (previous >= 0) panel.Children.RemoveAt(previous); panel.Children.Insert(index, button); }
            }
        }
        finally { applying = false; }
        PositionSelection();
    }
    internal void Clear() { buttons.Clear(); panel.Children.Clear(); retiring.Children.Clear(); selected = ""; positioned = false; highlight.Visibility = Visibility.Collapsed; }
    private void PositionSelection()
    {
        if (!buttons.TryGetValue(selected, out var target) || target.ActualWidth <= 0) { highlight.Visibility = Visibility.Collapsed; return; }
        var slot = LayoutInformation.GetLayoutSlot(target); var next = new Rect(slot.TopLeft, new Size(target.ActualWidth, target.ActualHeight)); highlight.Visibility = Visibility.Visible;
        if (positioned && next == lastSelection) return;
        if (!positioned || !IsLoaded) { selectionPosition.X = next.X; selectionPosition.Y = next.Y; highlight.Width = next.Width; highlight.Height = next.Height; }
        else
        {
            ProxyVisuals.Spring(selectionPosition, TranslateTransform.XProperty, next.X); ProxyVisuals.Spring(selectionPosition, TranslateTransform.YProperty, next.Y); ProxyVisuals.Spring(highlight, WidthProperty, next.Width); ProxyVisuals.Spring(highlight, HeightProperty, next.Height);
        }
        positioned = true; lastSelection = next;
    }
    private sealed class SourceButton : ToggleButton
    {
        private readonly ShareRing ring = new();
        private readonly TextBlock label = Ui.Text("", 14);
        private readonly TextBlock count = Ui.Muted("", 12);
        private readonly TextBlock rate = Ui.Muted("", 12);
        private readonly Grid content = new();
        private bool wide;
        private readonly ScaleTransform scale = new(1, 1);
        internal SourceButton()
        {
            Style = ConnectionVisuals.Style("Source"); Margin = new Thickness(0); MinWidth = 0; Focusable = true;
            label.TextWrapping = rate.TextWrapping = count.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = rate.TextTrimming = TextTrimming.CharacterEllipsis;
            label.LineHeight = 20; label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; rate.LineHeight = 16; rate.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition());
            ring.VerticalAlignment = count.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(ring); content.Children.Add(label); content.Children.Add(rate); content.Children.Add(count); Content = content;
            RenderTransform = scale; RenderTransformOrigin = new Point(.5, .5);
            MouseEnter += (_, _) => Surface(); MouseLeave += (_, _) => Surface(); Loaded += (_, _) => { DesignTokens.Changed += Theme; Surface(false); }; Unloaded += (_, _) => DesignTokens.Changed -= Theme;
        }
        private void Theme() { Palette(); Surface(false); }
        private void Palette()
        {
            if (wide || IsChecked == true) { Background = Brushes.Transparent; BorderBrush = Brushes.Transparent; }
            else { SetResourceReference(BackgroundProperty, "ControlBrush"); SetResourceReference(BorderBrushProperty, "ControlStrokeBrush"); }
        }
        private void Surface(bool animate = true)
        {
            if (Template?.FindName("Frame", this) is Border frame) ProxyVisuals.FadeBackground(frame, IsMouseOver ? TryFindResource(wide ? "SubtleHoverBrush" : "ControlHoverBrush") as Brush : Background, animate);
        }
        internal void SetWide(bool value)
        {
            wide = value; Height = value ? double.NaN : 32; MinHeight = value ? 50 : 32; Padding = value ? new Thickness(14, 7, 10, 7) : new Thickness(8, 0, 11, 0);
            ring.Width = ring.Height = value ? 34 : 18; ring.Thickness = value ? 4 : 3;
            content.ColumnDefinitions[1].Width = value ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            Grid.SetColumn(label, 1); Grid.SetColumn(rate, 1); Grid.SetColumn(count, 2); Grid.SetRow(rate, 1); Grid.SetRowSpan(ring, value ? 2 : 1); Grid.SetRowSpan(count, value ? 2 : 1);
            ring.Margin = new Thickness(0, 0, value ? 12 : 8, 0); label.Margin = new Thickness(0, 0, value ? 12 : 8, 0); rate.Visibility = value ? Visibility.Visible : Visibility.Collapsed; count.HorizontalAlignment = HorizontalAlignment.Right;
            content.RowDefinitions[0].Height = GridLength.Auto; content.RowDefinitions[1].Height = value ? new GridLength(17) : new GridLength(0); rate.Margin = new Thickness(0, value ? 1 : 0, 0, 0); Palette(); Surface(false);
        }
        internal void Set(ConnectionSource source, double total, bool selected)
        {
            IsChecked = selected; label.Text = source.Label; count.Text = "(" + source.Count.ToString() + ")"; var share = total > 0 ? Math.Min(1, Math.Max(0, source.Rate / total)) : 0;
            rate.Text = source.Rate >= 1 ? Math.Round(share * 100) + "% · " + ByteReading.Rate(source.Rate) : "—"; ring.Set(share); Palette(); Surface(false);
            AutomationProperties.SetName(this, source.Label + " (" + source.Count + ")"); ToolTip = source.Label + " (" + source.Count + ")";
        }
        internal void Enter() { ProxyVisuals.Tween(scale, ScaleTransform.ScaleXProperty, .9, 1, 250); ProxyVisuals.Tween(scale, ScaleTransform.ScaleYProperty, .9, 1, 250); ProxyVisuals.Tween(this, OpacityProperty, 0, 1, 250); }
        internal void Retire() { IsEnabled = false; IsHitTestVisible = false; Focusable = false; count.Visibility = rate.Visibility = Visibility.Hidden; }
    }
    private sealed class ShareRing : FrameworkElement
    {
        private static readonly DependencyProperty ShareProperty = DependencyProperty.Register("Share", typeof(double), typeof(ShareRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
        internal double Thickness { get; set; } = 3;
        private double target;
        internal ShareRing() { Loaded += (_, _) => DesignTokens.Changed += RefreshPalette; Unloaded += (_, _) => DesignTokens.Changed -= RefreshPalette; }
        private void RefreshPalette() => InvalidateVisual();
        internal void Set(double share) { if (target == share) return; target = share; ProxyVisuals.Tween(this, ShareProperty, (double)GetValue(ShareProperty), share, 900); }
        protected override void OnRender(DrawingContext drawing)
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2); var radius = Math.Max(0, (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2); if (radius <= 0) return;
            var track = TryFindResource("ControlStrongBrush") as Brush ?? Brushes.Gray; var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            drawing.DrawEllipse(null, new Pen(track, Thickness), center, radius, radius); var share = Math.Clamp((double)GetValue(ShareProperty), 0, 1); if (share <= 0) return;
            if (share >= .9999) { drawing.DrawEllipse(null, new Pen(accent, Thickness), center, radius, radius); return; }
            var angle = share * Math.PI * 2; var end = new Point(center.X - Math.Cos(angle) * radius, center.Y - Math.Sin(angle) * radius);
            var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(new Point(center.X - radius, center.Y), false, false); context.ArcTo(end, new Size(radius, radius), 0, share > .5, SweepDirection.Clockwise, true, false); }
            drawing.DrawGeometry(null, new Pen(accent, Thickness), geometry);
        }
    }
    private sealed class SourcePanel : Panel
    {
        internal bool Wide, NoWrap;
        internal Thickness Padding;
        internal event Action? Arranged;
        private readonly Dictionary<UIElement, (Point Position, TranslateTransform Move)> places = [];
        protected override Size MeasureOverride(Size available)
        {
            var width = double.IsFinite(available.Width) ? Math.Max(0, available.Width - Padding.Left - Padding.Right) : double.PositiveInfinity;
            double x = 0, y = 0, rowHeight = 0, used = 0; var gap = Wide ? 4d : 6d;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(Wide ? width : double.PositiveInfinity, double.PositiveInfinity)); var size = child.DesiredSize;
                if (!Wide && !NoWrap && x > 0 && x + size.Width > width) { used = Math.Max(used, x - gap); y += rowHeight + gap; x = rowHeight = 0; }
                if (Wide) { y += size.Height + gap; used = Math.Max(used, size.Width); }
                else { x += size.Width + gap; rowHeight = Math.Max(rowHeight, size.Height); }
            }
            used = Math.Max(used, Math.Max(0, x - gap)); var height = Wide ? Math.Max(0, y - gap) : y + rowHeight;
            return new Size((double.IsFinite(width) && !NoWrap ? width : used) + Padding.Left + Padding.Right, height + Padding.Top + Padding.Bottom);
        }
        protected override Size ArrangeOverride(Size final)
        {
            double x = Padding.Left, y = Padding.Top, rowHeight = 0, width = Math.Max(0, final.Width - Padding.Left - Padding.Right); var gap = Wide ? 4d : 6d;
            foreach (var stale in places.Keys.Where(child => !InternalChildren.Contains(child)).ToArray()) places.Remove(stale);
            foreach (UIElement child in InternalChildren)
            {
                var size = child.DesiredSize;
                if (!Wide && !NoWrap && x > Padding.Left && x + size.Width > final.Width - Padding.Right) { y += rowHeight + gap; x = Padding.Left; rowHeight = 0; }
                var point = new Point(x, y); child.Arrange(new Rect(point, new Size(Wide ? width : size.Width, size.Height)));
                if (!places.TryGetValue(child, out var previous))
                {
                    var move = new TranslateTransform(); var group = new TransformGroup(); if (child.RenderTransform != Transform.Identity) group.Children.Add(child.RenderTransform); group.Children.Add(move); child.RenderTransform = group; places[child] = (point, move);
                }
                else { if (previous.Position != point) { ProxyVisuals.Tween(previous.Move, TranslateTransform.XProperty, previous.Position.X - point.X + previous.Move.X, 0, 250); ProxyVisuals.Tween(previous.Move, TranslateTransform.YProperty, previous.Position.Y - point.Y + previous.Move.Y, 0, 250); } places[child] = (point, previous.Move); }
                if (Wide) y += size.Height + gap; else { x += size.Width + gap; rowHeight = Math.Max(rowHeight, size.Height); }
            }
            Arranged?.Invoke(); return final;
        }
    }
}
