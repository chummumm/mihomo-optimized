using System.Globalization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The uploaded 34/40-pixel DataGrid and connection readings, with native control peers.</summary>
internal static class ConnectionVisuals
{
    private static readonly ResourceDictionary templates = (ResourceDictionary)XamlReader.Parse("""
      <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:icons="clr-namespace:Mihomo.Desktop.Controls;assembly=mihomo-optimized">
        <Style x:Key="ResizeGrip" TargetType="Thumb"><Setter Property="Width" Value="12"/><Setter Property="Cursor" Value="SizeWE"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Grid Background="Transparent"><Border Width="2" Margin="0,8" CornerRadius="1" Background="{DynamicResource DividerBrush}" Opacity="0" x:Name="Line"/></Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Line" Property="Opacity" Value="1"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Line" Property="Opacity" Value="1"/><Setter TargetName="Line" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
        <Style x:Key="Header" TargetType="DataGridColumnHeader">
          <Setter Property="Height" Value="34"/><Setter Property="Padding" Value="10,0"/><Setter Property="FontSize" Value="12"/><Setter Property="FontWeight" Value="Normal"/>
          <Setter Property="Background" Value="{DynamicResource SolidCardBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridColumnHeader">
            <Grid Background="{TemplateBinding Background}"><Border BorderBrush="{DynamicResource DividerBrush}" BorderThickness="0,0,0,1"><Border x:Name="Hover" Padding="{TemplateBinding Padding}" Background="Transparent"><StackPanel Orientation="Horizontal" VerticalAlignment="Center"><TextBlock Text="{TemplateBinding Content}" FontSize="{TemplateBinding FontSize}" FontWeight="{TemplateBinding FontWeight}" Foreground="{TemplateBinding Foreground}" VerticalAlignment="Center" TextWrapping="NoWrap" TextTrimming="CharacterEllipsis"/><icons:FluentIcon x:Name="Sort" Margin="4,0,0,0" Width="11" Height="11" VerticalAlignment="Center" Visibility="Collapsed"/></StackPanel></Border></Border><Thumb x:Name="PART_LeftHeaderGripper" Style="{StaticResource ResizeGrip}" HorizontalAlignment="Left" Margin="-6,0,0,0"/><Thumb x:Name="PART_RightHeaderGripper" Style="{StaticResource ResizeGrip}" HorizontalAlignment="Right" Margin="0,0,-6,0"/></Grid>
            <ControlTemplate.Triggers><Trigger Property="SortDirection" Value="Ascending"><Setter TargetName="Sort" Property="Kind" Value="arrow-up"/><Setter TargetName="Sort" Property="Visibility" Value="Visible"/></Trigger><Trigger Property="SortDirection" Value="Descending"><Setter TargetName="Sort" Property="Kind" Value="arrow-down"/><Setter TargetName="Sort" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        <Style x:Key="Row" TargetType="DataGridRow"><Setter Property="Background" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/></Style>
        <Style x:Key="Cell" TargetType="DataGridCell">
          <Setter Property="Padding" Value="10,0"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Background" Value="Transparent"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridCell"><Border Padding="{TemplateBinding Padding}" Background="{TemplateBinding Background}"><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"/></Border></ControlTemplate></Setter.Value></Setter>
          <Style.Triggers><DataTrigger Binding="{Binding Closed}" Value="True"><Setter Property="Foreground" Value="{DynamicResource TextTertiaryBrush}"/></DataTrigger></Style.Triggers>
        </Style>
        <Style x:Key="HostCell" TargetType="DataGridCell" BasedOn="{StaticResource Cell}">
          <Setter Property="Padding" Value="18,0,10,0"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridCell"><Grid Background="{DynamicResource SolidCardBrush}"><Border Background="{Binding Background, RelativeSource={RelativeSource AncestorType=DataGridRow}}" Padding="{TemplateBinding Padding}"><ContentPresenter VerticalAlignment="Center"/></Border><Border x:Name="Selection" Width="3" Height="16" CornerRadius="2" HorizontalAlignment="Left" VerticalAlignment="Center" Margin="4,0,0,0" Background="{DynamicResource AccentBrush}" Visibility="Collapsed" IsHitTestVisible="False"/></Grid><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="Selection" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
        </Style>
        <Style x:Key="Source" TargetType="ToggleButton"><Setter Property="FontSize" Value="14"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton"><Grid><Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/></Border><Border x:Name="Focus" Margin="1" BorderThickness="2" BorderBrush="{DynamicResource TextPrimaryBrush}" CornerRadius="3" Visibility="Collapsed" IsHitTestVisible="False"/></Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
      </ResourceDictionary>
      """);
    static ConnectionVisuals()
    {
        ((Style)templates["Row"]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => GridState.Attach((Control)sender))));
        ((Style)templates["Header"]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => GridState.Attach((Control)sender))));
    }
    internal static Style Style(string key) => (Style)templates[key];
    internal static string Bytes(double bytes)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" }; bytes = Math.Max(0, bytes); var exponent = bytes < 1 ? 0 : Math.Min(4, (int)Math.Floor(Math.Log(bytes) / Math.Log(1024))); var value = Math.Floor(bytes / Math.Pow(1024, exponent) * 100) / 100;
        return value.ToString("F2", CultureInfo.InvariantCulture) + " " + units[exponent];
    }
    private sealed class GridState
    {
        private static readonly ConditionalWeakTable<Control, GridState> states = new();
        private readonly Control control;
        private bool subscribed;
        private readonly DependencyPropertyDescriptor? sort;
        private GridState(Control control)
        {
            this.control = control; control.MouseEnter += Pointer; control.MouseLeave += Pointer; control.Unloaded += (_, _) => Detach();
            if (control is DataGridRow row) { row.Selected += Selection; row.Unselected += Selection; }
            if (control is DataGridColumnHeader) sort = DependencyPropertyDescriptor.FromProperty(DataGridColumnHeader.SortDirectionProperty, typeof(DataGridColumnHeader));
        }
        internal static void Attach(Control control)
        {
            var state = states.GetValue(control, value => new GridState(value)); if (state.subscribed) return; state.subscribed = true; DesignTokens.Changed += state.Theme; state.sort?.AddValueChanged(control, state.SortChanged); state.Paint(false);
        }
        private void Detach() { if (!subscribed) return; subscribed = false; DesignTokens.Changed -= Theme; sort?.RemoveValueChanged(control, SortChanged); }
        private void Pointer(object sender, MouseEventArgs args) => Paint(true);
        private void Selection(object sender, RoutedEventArgs args) => Paint(true);
        private void Theme() => Paint(false);
        private void Paint(bool animate)
        {
            if (control is DataGridColumnHeader header)
            {
                if (header.Template.FindName("Hover", header) is Border frame) ProxyVisuals.FadeBackground(frame, header.IsMouseOver ? header.TryFindResource("SubtleHoverBrush") as Brush : Brushes.Transparent, animate);
                header.SetResourceReference(Control.ForegroundProperty, header.IsMouseOver ? "TextPrimaryBrush" : "TextSecondaryBrush");
            }
            else
            {
                var key = control is DataGridRow { IsSelected: true } ? "SubtlePressedBrush" : control.IsMouseOver ? "SubtleHoverBrush" : "Transparent";
                var next = (control.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Transparent; var previous = (control.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
                if (next == previous) return; var brush = new SolidColorBrush(next); control.Background = brush; if (animate && ProxyVisuals.Animate) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous, next, TimeSpan.FromMilliseconds(83)));
            }
        }
        private void SortChanged(object? sender, EventArgs args)
        {
            if (!ProxyVisuals.Animate || control.Template.FindName("Sort", control) is not FluentIcon icon) return;
            var scale = new ScaleTransform(1, 1); var turn = new RotateTransform(); var transform = new TransformGroup(); transform.Children.Add(scale); transform.Children.Add(turn);
            icon.RenderTransform = transform; icon.RenderTransformOrigin = new Point(.5, .5);
            ProxyVisuals.Tween(turn, RotateTransform.AngleProperty, -180, 0, 250); ProxyVisuals.Tween(scale, ScaleTransform.ScaleXProperty, .7, 1, 250); ProxyVisuals.Tween(scale, ScaleTransform.ScaleYProperty, .7, 1, 250); ProxyVisuals.Tween(icon, UIElement.OpacityProperty, .2, 1, 250);
        }
    }
    internal static TextBlock Label(string text, int size = 12, bool strong = false)
    {
        var label = Ui.Label(text, size, strong); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; return label;
    }
    internal static string RelativeTime(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.UtcNow - value; var seconds = Math.Max(0, elapsed.TotalSeconds);
        if (seconds < 45) return L.T("几秒前"); if (seconds < 90) return L.T("1 分钟前");
        if (seconds < 2700) return L.F("{0} 分钟前", Math.Round(elapsed.TotalMinutes)); if (seconds < 5400) return L.T("1 小时前");
        if (seconds < 79200) return L.F("{0} 小时前", Math.Round(elapsed.TotalHours)); if (seconds < 129600) return L.T("1 天前");
        if (seconds < 2246400) return L.F("{0} 天前", Math.Round(elapsed.TotalDays)); if (seconds < 3888000) return L.T("1 个月前");
        return seconds < 27648000 ? L.F("{0} 个月前", Math.Round(elapsed.TotalDays / 30)) : L.F("{0} 年前", Math.Max(1, Math.Round(elapsed.TotalDays / 365)));
    }
}

public sealed class ConnectionHostCell : Control
{
    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(string), typeof(ConnectionHostCell), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, NameChanged));
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(string), typeof(ConnectionHostCell), new FrameworkPropertyMetadata("open", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Host { get => (string)GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public ConnectionHostCell() { Height = 39; ClipToBounds = true; FontSize = 13; SetResourceReference(FontFamilyProperty, "BodyFont"); Loaded += (_, _) => DesignTokens.Changed += Refresh; Unloaded += (_, _) => DesignTokens.Changed -= Refresh; }
    private void Refresh() => InvalidateVisual();
    private static void NameChanged(DependencyObject source, DependencyPropertyChangedEventArgs args) => AutomationProperties.SetName(source, (string)args.NewValue);
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 18) return;
        Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var closed = State == "closed"; var dot = Resource(closed ? "TextTertiaryBrush" : State == "flow" ? "AccentBrush" : "SuccessBrush");
        drawing.DrawEllipse(closed ? null : dot, closed ? new Pen(dot, 1) : null, new Point(4, ActualHeight / 2), closed ? 3.5 : 4, closed ? 3.5 : 4);
        var text = new FormattedText(Host, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, Resource(closed ? "TextTertiaryBrush" : "TextPrimaryBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, ActualWidth - 18), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        drawing.DrawText(text, new Point(18, (ActualHeight - text.Height) / 2));
        if (closed) { var brush = Resource("TextTertiaryBrush").CloneCurrentValue(); brush.Opacity *= .6; drawing.DrawLine(new Pen(brush, 1), new Point(18, ActualHeight / 2), new Point(Math.Min(ActualWidth, 18 + text.Width), ActualHeight / 2)); }
    }
}

internal sealed class ConnectionRateReading : TextBlock
{
    private TextBlock? unitLabel;
    internal ConnectionRateReading() { FontSize = 28; FontWeight = FontWeights.SemiBold; SetResourceReference(FontFamilyProperty, "DisplayFont"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); LineHeight = 36; LineStackingStrategy = LineStackingStrategy.MaxHeight; }
    internal void SetMetrics(bool phone)
    {
        FontSize = phone ? 20 : 28; LineHeight = 36;
        if (unitLabel is not null)
        {
            unitLabel.LineHeight = LineHeight;
            // CSS gives each inline run its own 36-DIP line box, then unions
            // those boxes at a common baseline. WPF MaxHeight only accounts for
            // the glyph box of a Run, so it under-measures this mixed-size line.
            // Keep its correctly aligned glyphs and reserve the missing leading
            // using the actual fallback fonts rather than a fixed pixel inset.
            double Baseline(TextBlock text)
            {
                var metrics = new FormattedText("0", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                    text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                return metrics.Baseline + (LineHeight - metrics.Height) / 2;
            }
            MinHeight = LineHeight + Math.Abs(Baseline(this) - Baseline(unitLabel));
        }
    }
    internal void Set(double value)
    {
        var text = ByteReading.Rate(value); if (text == Tag as string) return; Tag = text;
        var separator = text.LastIndexOf(' '); Inlines.Clear(); Inlines.Add(new Run(text[..separator]));
        // The source's smaller unit inherits the reading's absolute line-height.
        // Baseline-align both line boxes and allow their union to set row height.
        unitLabel = Ui.Muted(text[(separator + 1)..], 13); unitLabel.Margin = new Thickness(4, 0, 0, 0); unitLabel.FontWeight = FontWeights.Normal; unitLabel.LineHeight = LineHeight;
        Inlines.Add(new InlineUIContainer(unitLabel) { BaselineAlignment = BaselineAlignment.Baseline }); AutomationProperties.SetName(this, text);
    }
}

internal sealed class ConnectionTotal : StackPanel
{
    private readonly FluentIcon arrow;
    private readonly TextBlock value = Ui.Muted("0 B", 12);
    private readonly bool upload;
    private double previous = double.NaN;
    private readonly TranslateTransform movement = new();
    internal ConnectionTotal(bool upload)
    {
        this.upload = upload; Orientation = Orientation.Horizontal;
        value.LineHeight = 17.16;
        arrow = new FluentIcon(upload ? "arrow-up" : "arrow-down", 12) { Margin = new Thickness(0, 0, 6, 0), RenderTransform = movement, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(Control.ForegroundProperty, upload ? "TextSecondaryBrush" : "AccentLinkBrush"); Children.Add(arrow); Children.Add(value);
    }
    internal void Set(double bytes)
    {
        value.Text = ConnectionVisuals.Bytes(bytes);
        if (bytes == previous) return; var animate = double.IsFinite(previous) && IsVisible && ProxyVisuals.Animate; previous = bytes;
        if (!animate) return;
        var offset = upload ? -6d : 6d;
        var move = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        move.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0))); move.KeyFrames.Add(new SplineDoubleKeyFrame(offset, KeyTime.FromPercent(.45), new KeySpline(0, 0, 0, 1))); move.KeyFrames.Add(new DiscreteDoubleKeyFrame(-offset, KeyTime.FromPercent(.46))); move.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0))); opacity.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(.45), new KeySpline(0, 0, 0, 1))); opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(.46))); opacity.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), new KeySpline(0, 0, 0, 1)));
        movement.BeginAnimation(TranslateTransform.YProperty, move); arrow.BeginAnimation(OpacityProperty, opacity);
    }
}
