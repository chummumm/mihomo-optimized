using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mihomo.Desktop.Controls;

/// <summary>The Settings ButtonSelect from the supplied Fluent design, with native selection peers.</summary>
internal sealed class SettingsSegments : ListBox
{
    private static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch), typeof(bool), typeof(SettingsSegments),
        new PropertyMetadata(false, (owner, args) => ((SettingsSegments)owner).ApplyStretch((bool)args.NewValue)));
    internal bool Stretch { get => (bool)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    private static readonly ResourceDictionary templates = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="Segments" TargetType="ListBox">
            <Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="2"/>
            <Setter Property="Background" Value="{DynamicResource ControlAltBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource ControlStrokeBrush}"/>
            <Setter Property="ItemsPanel"><Setter.Value><ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate></Setter.Value></Setter>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBox">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                <Grid>
                  <Canvas IsHitTestVisible="False">
                    <Border x:Name="PART_Selection" Height="28" CornerRadius="4" BorderThickness="1" BorderBrush="{DynamicResource ControlStrokeBrush}" Background="{DynamicResource ControlBrush}" Visibility="Hidden">
                      <Grid>
                        <Border Height="1" VerticalAlignment="Bottom" Background="{DynamicResource ControlElevationBrush}"/>
                        <Border Width="16" Height="3" CornerRadius="2" VerticalAlignment="Bottom" Margin="0,0,0,2" Background="{DynamicResource AccentBrush}"/>
                      </Grid>
                    </Border>
                  </Canvas>
                  <ItemsPresenter/>
                </Grid>
              </Border>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Segment" TargetType="ListBoxItem">
            <Setter Property="Height" Value="28"/><Setter Property="Padding" Value="14,0"/>
            <Setter Property="FontSize" Value="14"/><Setter Property="FontWeight" Value="Normal"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
              <Border x:Name="Frame" CornerRadius="4" Background="Transparent" Padding="{TemplateBinding Padding}" BorderBrush="Transparent" BorderThickness="0">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FocusOuterBrush}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/><Setter Property="Padding" Value="12,0"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
    private Border? selection;
    private bool placed;
    private readonly Dictionary<ListBoxItem, SegmentState> states = [];

    internal SettingsSegments()
    {
        Style = (Style)templates["Segments"];
        ItemContainerStyle = (Style)templates["Segment"];
        SelectionMode = SelectionMode.Single;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        IsTextSearchEnabled = false;
        Loaded += (_, _) => PlaceSelection(false);
        SizeChanged += (_, _) => PlaceSelection(false);
        SelectionChanged += (_, _) => _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => PlaceSelection(true)));
    }

    private void ApplyStretch(bool stretch)
    {
        HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        if (stretch)
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse("<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><UniformGrid Rows=\"1\"/></ItemsPanelTemplate>");
        else ClearValue(ItemsPanelProperty);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => PlaceSelection(false)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        selection = GetTemplateChild("PART_Selection") as Border;
        placed = false;
        PlaceSelection(false);
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ListBoxItem container)
        {
            container.Margin = new Thickness(0, 0, Items.IndexOf(item) == Items.Count - 1 ? 0 : 2, 0);
            if (!states.ContainsKey(container)) states.Add(container, new SegmentState(container));
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ListBoxItem container && states.Remove(container, out var state)) state.Dispose();
        base.ClearContainerForItemOverride(element, item);
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => PlaceSelection(false)));
    }

    private void PlaceSelection(bool animate)
    {
        if (selection?.Parent is not Canvas canvas || !IsLoaded || SelectedIndex < 0
            || ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not FrameworkElement item || item.ActualWidth <= 0) return;
        double left = item.TranslatePoint(new Point(), canvas).X;
        double width = item.ActualWidth;
        double previousLeft = Canvas.GetLeft(selection);
        double previousWidth = selection.ActualWidth;
        selection.BeginAnimation(Canvas.LeftProperty, null);
        selection.BeginAnimation(WidthProperty, null);
        Canvas.SetLeft(selection, left); selection.Width = width; selection.Visibility = Visibility.Visible;
        if (animate && placed && FluentMotion.Enabled && double.IsFinite(previousLeft) && previousWidth > 0)
        {
            // ButtonSelect uses springs.snap: 167ms cubic-bezier(0,0,0,1).
            Animate(selection, Canvas.LeftProperty, previousLeft, left, 167);
            Animate(selection, WidthProperty, previousWidth, width, 167);
        }
        placed = true;
    }

    internal static void Animate(Animatable target, DependencyProperty property, double from, double to, double milliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        target.BeginAnimation(property, animation);
    }

    internal static void Animate(FrameworkElement target, DependencyProperty property, double from, double to, double milliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        target.BeginAnimation(property, animation);
    }

    private sealed class SegmentState : IDisposable
    {
        private readonly ListBoxItem item;
        private Border? frame;
        private bool attached;

        internal SegmentState(ListBoxItem item)
        {
            this.item = item;
            item.Loaded += Loaded; item.Unloaded += Unloaded;
            if (item.IsLoaded) Attach();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) => Detach();
        private void Attach()
        {
            if (attached) return;
            item.ApplyTemplate();
            frame = item.Template?.FindName("Frame", item) as Border;
            if (frame is null) return;
            attached = true;
            item.MouseEnter += Mouse; item.MouseLeave += Mouse;
            item.Selected += Selection; item.Unselected += Selection;
            item.IsEnabledChanged += Enabled;
            DesignTokens.Changed += ThemeChanged;
            SystemParameters.StaticPropertyChanged += SystemSettingChanged;
            Update(false);
        }

        private void Mouse(object sender, MouseEventArgs args) => Update(true);
        private void Selection(object sender, RoutedEventArgs args) => Update(true);
        private void Enabled(object sender, DependencyPropertyChangedEventArgs args) => Update(true);
        private void ThemeChanged() => Update(false);
        private void SystemSettingChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) Update(false);
        }

        private void Update(bool animate)
        {
            if (!attached || frame is null) return;
            bool enabled = item.IsEnabled, selected = item.IsSelected, hover = enabled && item.IsMouseOver;
            string foreground = !enabled ? "DisabledTextBrush" : selected || hover ? "TextPrimaryBrush" : "TextSecondaryBrush";
            var background = hover && !selected ? ResourceColor("SubtleHoverBrush") : Colors.Transparent;
            // ButtonSelect CSS: background-color dur-1 linear; color dur-2 linear.
            Paint(frame, Border.BackgroundProperty, background, 83, animate);
            Paint(item, Control.ForegroundProperty, ResourceColor(foreground), 167, animate);
        }

        private Color ResourceColor(string key) => item.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;

        private static void Paint(DependencyObject target, DependencyProperty property, Color destination, double milliseconds, bool animate)
        {
            var from = target.GetValue(property) is SolidColorBrush current ? current.Color : destination;
            var brush = new SolidColorBrush(destination);
            target.SetCurrentValue(property, brush);
            if (animate && FluentMotion.Enabled && from != destination)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, destination, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop });
        }

        private void Detach()
        {
            if (!attached) return;
            Update(false);
            attached = false;
            item.MouseEnter -= Mouse; item.MouseLeave -= Mouse;
            item.Selected -= Selection; item.Unselected -= Selection;
            item.IsEnabledChanged -= Enabled;
            DesignTokens.Changed -= ThemeChanged;
            SystemParameters.StaticPropertyChanged -= SystemSettingChanged;
        }

        public void Dispose()
        {
            Detach();
            item.Loaded -= Loaded; item.Unloaded -= Unloaded;
            item.ClearValue(Control.ForegroundProperty);
            frame?.ClearValue(Border.BackgroundProperty);
        }
    }
}

/// <summary>The original attention keyframes: a 2px inset and a fading 10px spread, twice.</summary>
internal sealed class SettingsAttention : FrameworkElement
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(SettingsAttention),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    internal SettingsAttention() { IsHitTestVisible = false; }

    internal void Start()
    {
        Stop();
        if (!FluentMotion.Enabled) return;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1400), RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        BeginAnimation(ProgressProperty, animation);
    }
    internal void Stop() { BeginAnimation(ProgressProperty, null); Progress = 1; }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Progress >= 1 || ActualWidth <= 2 || ActualHeight <= 2 || TryFindResource("AccentBrush") is not Brush accent) return;
        double progress = Math.Clamp(Progress, 0, 1), spread = 10 * progress;
        var stroke = accent.CloneCurrentValue(); stroke.Opacity *= 1 - progress;
        drawing.DrawRoundedRectangle(null, new Pen(stroke, 2), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 3, 3);
        if (spread <= 0) return;
        var outer = new RectangleGeometry(new Rect(-spread, -spread, ActualWidth + spread * 2, ActualHeight + spread * 2), 4 + spread, 4 + spread);
        var inner = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);
        var halo = accent.CloneCurrentValue(); halo.Opacity *= .7 * (1 - progress);
        drawing.DrawGeometry(halo, null, new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
    }
}

/// <summary>Centers search targets smoothly without allowing focus to first jump the viewport.</summary>
internal sealed class SettingsScrollViewer : ScrollViewer
{
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(nameof(AnimatedOffset), typeof(double), typeof(SettingsScrollViewer),
        new PropertyMetadata(0d, (owner, args) => ((SettingsScrollViewer)owner).ScrollToVerticalOffset((double)args.NewValue)));
    private double AnimatedOffset { get => (double)GetValue(AnimatedOffsetProperty); set => SetValue(AnimatedOffsetProperty, value); }
    private bool centering;

    internal SettingsScrollViewer()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, args) => { if (centering) args.Handled = true; }));
        PreviewMouseWheel += (_, _) => CancelAnimation();
        PreviewMouseDown += (_, _) => CancelAnimation();
        PreviewKeyDown += (_, args) => { if (args.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End) CancelAnimation(); };
    }

    internal void Center(FrameworkElement element)
    {
        CancelAnimation(); UpdateLayout();
        var point = element.TransformToAncestor(this).Transform(new Point());
        double target = Math.Clamp(VerticalOffset + point.Y - (ViewportHeight - element.ActualHeight) / 2, 0, ScrollableHeight);
        centering = true;
        try { element.Focus(); } finally { centering = false; }
        if (!FluentMotion.Enabled) { ScrollToVerticalOffset(target); return; }
        double from = VerticalOffset;
        AnimatedOffset = target;
        SettingsSegments.Animate(this, AnimatedOffsetProperty, from, target, 250);
    }

    internal void CancelAnimation()
    {
        double current = VerticalOffset;
        BeginAnimation(AnimatedOffsetProperty, null);
        AnimatedOffset = current;
    }
}
