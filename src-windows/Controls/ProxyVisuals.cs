using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>Measurements and motion copied from the uploaded proxy and editor components.</summary>
internal static class ProxyVisuals
{
    internal static bool Animate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static double templateLoadMilliseconds;
    private static readonly ResourceDictionary templates = LoadTemplates();
    internal static double TemplateLoadMilliseconds => templateLoadMilliseconds;
    private static ResourceDictionary LoadTemplates()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var resource = new ResourceDictionary { Source = new Uri("/mihomo-optimized;component/Controls/ProxyTemplates.xaml", UriKind.Relative) };
        templateLoadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return resource;
    }
    internal static Style Style(string key) => (Style)templates[key];
    internal static void SetBusyIcon(FluentIcon icon, bool busy) => BusyIconMotion.Set(icon, busy);
    private sealed class BusyIconMotion
    {
        private static readonly ConditionalWeakTable<FluentIcon, BusyIconMotion> states = new();
        private readonly FluentIcon icon;
        private readonly ScaleTransform scale = new(1, 1);
        private readonly RotateTransform rotation = new();
        private bool busy;
        private bool playing;
        private bool subscribed;
        private BusyIconMotion(FluentIcon icon)
        {
            this.icon = icon;
            var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(rotation);
            icon.RenderTransform = transforms; icon.RenderTransformOrigin = new Point(.5, .5);
            icon.Loaded += Loaded; icon.Unloaded += Unloaded; icon.IsVisibleChanged += (_, _) => Update();
            if (icon.IsLoaded) Attach();
        }
        internal static void Set(FluentIcon icon, bool busy)
        {
            var state = states.GetValue(icon, value => new BusyIconMotion(value));
            state.busy = busy; state.Update();
        }
        private void Loaded(object sender, RoutedEventArgs e) => Attach();
        private void Attach()
        {
            if (!subscribed) { SystemParameters.StaticPropertyChanged += PreferenceChanged; subscribed = true; }
            Update();
        }
        private void Unloaded(object sender, RoutedEventArgs e)
        {
            if (subscribed) SystemParameters.StaticPropertyChanged -= PreferenceChanged;
            subscribed = false; Stop();
        }
        private void PreferenceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
                icon.Dispatcher.InvokeAsync(Update);
        }
        private void Update()
        {
            bool active = busy && icon.IsLoaded && icon.IsVisible && Animate;
            if (playing == active) return;
            Stop();
            if (!active) return;
            playing = true;
            if (icon.Kind == "update")
                rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            else if (icon.Kind == "speed")
            {
                icon.BeginAnimation(UIElement.OpacityProperty, Charge(.55));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Charge(.88));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Charge(.88));
            }
            else if (icon.Kind == "healthcheck")
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Heartbeat());
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Heartbeat());
            }
        }
        private static DoubleAnimationUsingKeyFrames Heartbeat()
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100), RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            foreach (var (fraction, value) in new[] { (.14, 1.2), (.28, .96), (.42, 1.12), (.6, 1d), (1d, 1d) })
                animation.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100 * fraction)), new KeySpline(0, 0, 0, 1)));
            return animation;
        }
        private static DoubleAnimationUsingKeyFrames Charge(double midpoint)
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900), RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(midpoint, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), new KeySpline(.8, 0, .2, 1)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new KeySpline(.8, 0, .2, 1)));
            return animation;
        }
        private void Stop()
        {
            playing = false;
            icon.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
    internal static string Tone(string tone) => tone switch { "good" => "SuccessBrush", "warn" => "WarningBrush", "bad" => "DangerBrush", "live" => "AccentBrush", "quiet" => "FaintBrush", _ => "TextTertiaryBrush" };
    internal static void Surface(ButtonBase button, string hover, string pressed) => _ = new SurfaceTransition(button, hover, pressed);
    internal static void FadeBackground(Border frame, Brush? target, bool animate = true)
    {
        var next = (target as SolidColorBrush)?.Color ?? Colors.Transparent;
        var from = (frame.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        if (next == from) return;
        var brush = new SolidColorBrush(next); frame.Background = brush;
        if (animate && Animate) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, next, TimeSpan.FromMilliseconds(83)));
    }
    private sealed class SurfaceTransition
    {
        private readonly ButtonBase button;
        private readonly string hover, pressed;
        private readonly System.ComponentModel.DependencyPropertyDescriptor state;
        private bool attached;
        internal SurfaceTransition(ButtonBase button, string hover, string pressed)
        {
            this.button = button; this.hover = hover; this.pressed = pressed;
            state = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, button.GetType());
            button.Loaded += (_, _) => Attach(); button.Unloaded += (_, _) => Detach();
            if (button.IsLoaded) Attach();
        }
        private void Attach()
        {
            if (attached) return; attached = true; button.ApplyTemplate();
            button.MouseEnter += Mouse; button.MouseLeave += Mouse; state.AddValueChanged(button, Changed); DesignTokens.Changed += Theme; Update(false);
        }
        private void Detach()
        {
            if (!attached) return; attached = false; button.MouseEnter -= Mouse; button.MouseLeave -= Mouse; state.RemoveValueChanged(button, Changed); DesignTokens.Changed -= Theme;
        }
        private void Mouse(object sender, MouseEventArgs args) => Update(true);
        private void Changed(object? sender, EventArgs args) => Update(true);
        private void Theme() => Update(false);
        private void Update(bool animate)
        {
            if (button.Template.FindName("Frame", button) is Border frame)
                FadeBackground(frame, button.IsPressed ? button.TryFindResource(pressed) as Brush : button.IsMouseOver ? button.TryFindResource(hover) as Brush : button.Background, animate);
        }
    }
    internal static double ContainerWidth(FrameworkElement element, double fallback = 0)
    {
        for (DependencyObject? item = element; item is not null; item = VisualTreeHelper.GetParent(item))
            if (item is Mihomo.Desktop.Pages.NativePage page && page.PageContainerWidth > 0) return page.PageContainerWidth;
        return fallback > 0 ? fallback : element.ActualWidth;
    }
    internal static void Attention(FrameworkElement element)
    {
        if (!Animate || AdornerLayer.GetAdornerLayer(element) is not { } layer) return;
        foreach (var old in layer.GetAdorners(element)?.OfType<AttentionBorder>() ?? []) layer.Remove(old);
        var border = new AttentionBorder(element); layer.Add(border);
        Tween(border, UIElement.OpacityProperty, 1, 0, 1200, () => layer.Remove(border));
    }
    private sealed class AttentionBorder(FrameworkElement element) : Adorner(element)
    {
        protected override void OnRender(DrawingContext drawing)
        {
            IsHitTestVisible = false;
            var brush = (Brush?)element.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
            drawing.DrawRoundedRectangle(null, new Pen(brush, 2), new Rect(1, 1, Math.Max(0, element.ActualWidth - 2), Math.Max(0, element.ActualHeight - 2)), 3, 3);
        }
    }
    internal static void Tween(Animatable target, DependencyProperty property, double from, double to, int milliseconds, Point? first = null, Point? second = null, Action? complete = null)
    {
        target.BeginAnimation(property, null); target.SetValue(property, to);
        if (!Animate || Math.Abs(from - to) < .001) { complete?.Invoke(); return; }
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), new KeySpline(first ?? new Point(0, 0), second ?? new Point(0, 1))));
        animation.Completed += (_, _) => { target.BeginAnimation(property, null); complete?.Invoke(); };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    internal static void Tween(UIElement target, DependencyProperty property, double from, double to, int milliseconds, Action? complete = null, Point? first = null, Point? second = null)
    {
        target.BeginAnimation(property, null); target.SetValue(property, to);
        if (!Animate || Math.Abs(from - to) < .001) { complete?.Invoke(); return; }
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), new KeySpline(first ?? new Point(0, 0), second ?? new Point(0, 1))));
        animation.Completed += (_, _) => { target.BeginAnimation(property, null); complete?.Invoke(); };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    internal static void Spring(Animatable target, DependencyProperty property, double to, string kind = "snap")
    {
        double from = (double)target.GetValue(property); target.BeginAnimation(property, null); target.SetValue(property, to);
        if (!Animate || Math.Abs(from - to) < .01) return;
        target.BeginAnimation(property, Motion(from, to, kind), HandoffBehavior.SnapshotAndReplace);
    }
    internal static void Spring(FrameworkElement target, DependencyProperty property, double to, string kind = "snap")
    {
        double from = (double)target.GetValue(property); if (double.IsNaN(from)) from = property == FrameworkElement.HeightProperty ? target.ActualHeight : target.ActualWidth;
        target.BeginAnimation(property, null); target.SetValue(property, to);
        if (!Animate || Math.Abs(from - to) < .01) return;
        target.BeginAnimation(property, Motion(from, to, kind), HandoffBehavior.SnapshotAndReplace);
    }
    private static DoubleAnimationUsingKeyFrames Motion(double from, double to, string kind)
    {
        // The final uploaded patch replaces the earlier springs with WinUI tweens.
        var milliseconds = kind == "snap" ? 167 : 250;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), kind == "glide" ? new KeySpline(.55, .55, 0, 1) : new KeySpline(0, 0, 0, 1)));
        return animation;
    }
    internal static void Reading(UIElement element)
    {
        if (!Animate) return;
        var move = new TranslateTransform(); element.RenderTransform = move;
        Tween(move, TranslateTransform.YProperty, 3, 0, 250); Tween(element, UIElement.OpacityProperty, 0, 1, 250);
    }
    internal static void Press(ButtonBase button, double scale)
    {
        var transform = new ScaleTransform(1, 1); button.RenderTransform = transform; button.RenderTransformOrigin = new Point(.5, .5);
        void Set(double value) { Spring(transform, ScaleTransform.ScaleXProperty, value); Spring(transform, ScaleTransform.ScaleYProperty, value); }
        button.PreviewMouseLeftButtonDown += (_, _) => Set(scale);
        button.PreviewMouseLeftButtonUp += (_, _) => Set(1); button.LostMouseCapture += (_, _) => Set(1); button.MouseLeave += (_, _) => Set(1);
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) Set(scale); }; button.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) Set(1); };
    }
}

/// <summary>Native Expander peer with the uploaded 250 ms reveal and 167 ms collapse.</summary>
internal sealed class ProxyExpander : Expander
{
    internal bool AnimateHeight { get; init; } = true;
    private Border? reveal, header;
    private ContentPresenter? body;
    private RotateTransform? caret;
    private long transition;
    public ProxyExpander()
    {
        Style = ProxyVisuals.Style("Expander");
        Expanded += (_, _) => Reveal(true); Collapsed += (_, _) => Reveal(false);
        PreviewKeyDown += (_, e) => { if (IsKeyboardFocused && e.Key is Key.Enter or Key.Space) { SetCurrentValue(IsExpandedProperty, !IsExpanded); e.Handled = true; } };
    }
    internal void SetCaret(FluentIcon icon) { caret = new RotateTransform(IsExpanded ? 180 : 0); icon.RenderTransformOrigin = new Point(.5, .5); icon.RenderTransform = caret; }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); reveal = GetTemplateChild("Reveal") as Border; body = GetTemplateChild("Body") as ContentPresenter; header = GetTemplateChild("HeaderFrame") as Border;
        if (header is not null)
        {
            header.MouseEnter += (_, _) => ProxyVisuals.FadeBackground(header, TryFindResource("CardHoverBrush") as Brush);
            header.MouseLeave += (_, _) => ProxyVisuals.FadeBackground(header, Brushes.Transparent);
            header.MouseLeftButtonDown += (_, e) => { if (!InsideButton(e.OriginalSource)) ProxyVisuals.FadeBackground(header, TryFindResource("SubtleSurfaceBrush") as Brush); };
            header.MouseLeftButtonUp += (_, e) =>
            {
                if (InsideButton(e.OriginalSource)) return;
                ProxyVisuals.FadeBackground(header, TryFindResource("CardHoverBrush") as Brush); Focus(); SetCurrentValue(IsExpandedProperty, !IsExpanded); e.Handled = true;
            };
        }
        if (reveal is not null) { reveal.Height = IsExpanded ? double.NaN : 0; reveal.IsHitTestVisible = IsExpanded; }
    }
    private bool InsideButton(object source)
    {
        for (var item = source as DependencyObject; item is not null && item != header; item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item)) if (item is ButtonBase) return true;
        return false;
    }
    private void Reveal(bool expanded)
    {
        if (caret is not null) ProxyVisuals.Tween(caret, RotateTransform.AngleProperty, caret.Angle, expanded ? 180 : 0, 167, new Point(.55, .55), new Point(0, 1));
        if (reveal is null || body is null) return;
        var generation = ++transition; reveal.IsHitTestVisible = expanded;
        var from = reveal.ActualHeight;
        body.Measure(new Size(Math.Max(0, ActualWidth - 2), double.PositiveInfinity));
        var height = expanded ? body.DesiredSize.Height : 0;
        if (!IsLoaded || !ProxyVisuals.Animate) { reveal.BeginAnimation(HeightProperty, null); reveal.Height = expanded ? double.NaN : 0; body.Opacity = expanded ? 1 : 0; return; }
        if (!AnimateHeight)
        {
            reveal.BeginAnimation(HeightProperty, null); reveal.Height = expanded ? double.NaN : 0;
            if (expanded) { var offset = new TranslateTransform(); body.RenderTransform = offset; TweenBody(offset); }
            return;
        }
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(expanded ? 250 : 167) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(height, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(expanded ? 250 : 167)), expanded ? new KeySpline(0, 0, 0, 1) : new KeySpline(1, 0, 1, 1)));
        reveal.Height = height; animation.Completed += (_, _) => { if (generation != transition) return; reveal.BeginAnimation(HeightProperty, null); reveal.Height = expanded ? double.NaN : 0; };
        reveal.BeginAnimation(HeightProperty, animation);
        var move = new TranslateTransform(); body.RenderTransform = move;
        if (expanded) ProxyVisuals.Tween(move, TranslateTransform.YProperty, -8, 0, 250);
        ProxyVisuals.Tween(body, OpacityProperty, expanded ? 0 : body.Opacity, expanded ? 1 : 0, expanded ? 250 : 83);
    }
    private void TweenBody(TranslateTransform offset)
    {
        if (body is null) return;
        ProxyVisuals.Tween(offset, TranslateTransform.YProperty, -8, 0, 250);
        ProxyVisuals.Tween(body, OpacityProperty, 0, 1, 250);
    }
}
