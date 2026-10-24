using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mihomo.Desktop.Controls;

/// <summary>The rule list keeps the reference's fixed column insets and has no sortable header state.</summary>
internal static class RuleVisuals
{
    private static readonly ResourceDictionary templates = (ResourceDictionary)XamlReader.Parse("""
      <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
        <Style x:Key="Header" TargetType="DataGridColumnHeader">
          <Setter Property="Height" Value="34"/><Setter Property="FontSize" Value="12"/><Setter Property="FontWeight" Value="Normal"/>
          <Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/><Setter Property="Background" Value="{DynamicResource SolidCardBrush}"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridColumnHeader"><Border Background="{TemplateBinding Background}" BorderBrush="{DynamicResource DividerBrush}" BorderThickness="0,0,0,1" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"/></Border></ControlTemplate></Setter.Value></Setter>
        </Style>
        <Style x:Key="Cell" TargetType="DataGridCell">
          <Setter Property="BorderThickness" Value="0"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Background" Value="Transparent"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridCell"><Border Padding="{TemplateBinding Padding}" Background="Transparent"><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"/></Border></ControlTemplate></Setter.Value></Setter>
        </Style>
        <Style x:Key="Row" TargetType="DataGridRow"><Setter Property="Background" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/></Style>
      </ResourceDictionary>
      """);
    static RuleVisuals() => ((Style)templates["Row"]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => RowState.Attach((DataGridRow)sender))));
    internal static Style Style(string key) => (Style)templates[key];
    private sealed class RowState
    {
        private static readonly ConditionalWeakTable<DataGridRow, RowState> states = new();
        private readonly DataGridRow row;
        private bool subscribed;
        private RowState(DataGridRow row) { this.row = row; row.MouseEnter += Pointer; row.MouseLeave += Pointer; row.Unloaded += (_, _) => { if (subscribed) DesignTokens.Changed -= Theme; subscribed = false; }; }
        internal static void Attach(DataGridRow row) { var state = states.GetValue(row, value => new RowState(value)); if (!state.subscribed) { state.subscribed = true; DesignTokens.Changed += state.Theme; } state.Paint(false); }
        private void Pointer(object sender, MouseEventArgs e) => Paint(true);
        private void Theme() => Paint(false);
        private void Paint(bool animate)
        {
            var next = row.IsMouseOver && row.TryFindResource("SubtleHoverBrush") is SolidColorBrush fill ? fill.Color : Colors.Transparent;
            var previous = (row.Background as SolidColorBrush)?.Color ?? Colors.Transparent; if (previous == next) return;
            var brush = new SolidColorBrush(next); row.Background = brush;
            if (animate && ProxyVisuals.Animate) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous, next, TimeSpan.FromMilliseconds(83)));
        }
    }
}

/// <summary>Two flex groups: wrap the metadata onto its own line only when they no longer fit.</summary>
internal sealed class RuleProviderLayout : Panel
{
    private bool wrapped;
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 1200;
        foreach (UIElement child in Children) child.Measure(new Size(width, double.PositiveInfinity));
        wrapped = Children.Count == 2 && Children[0].DesiredSize.Width + Children[1].DesiredSize.Width + 16 > width;
        return new Size(width, wrapped ? Children[0].DesiredSize.Height + 8 + Children[1].DesiredSize.Height : Children.Cast<UIElement>().Select(child => child.DesiredSize.Height).DefaultIfEmpty().Max());
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        var first = Children[0]; var second = Children[1];
        first.Arrange(new Rect(0, wrapped ? 0 : Math.Max(0, (finalSize.Height - first.DesiredSize.Height) / 2), Math.Min(finalSize.Width, first.DesiredSize.Width), first.DesiredSize.Height));
        second.Arrange(new Rect(wrapped ? 0 : Math.Max(0, finalSize.Width - second.DesiredSize.Width), wrapped ? first.DesiredSize.Height + 8 : Math.Max(0, (finalSize.Height - second.DesiredSize.Height) / 2), Math.Min(finalSize.Width, second.DesiredSize.Width), second.DesiredSize.Height));
        return finalSize;
    }
}

internal sealed class RuleProviderHeader : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in Children) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(Math.Min(available.Width, Children.Cast<UIElement>().Sum(child => child.DesiredSize.Width) + Math.Max(0, Children.Count - 1) * 8), Children.Cast<UIElement>().Select(child => child.DesiredSize.Height).DefaultIfEmpty().Max());
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double fixedWidth = Children.Cast<UIElement>().Skip(1).Sum(child => child.DesiredSize.Width) + Math.Max(0, Children.Count - 1) * 8, x = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            var child = Children[index]; var width = index == 0 ? Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - fixedWidth)) : child.DesiredSize.Width;
            child.Arrange(new Rect(x, Math.Max(0, (finalSize.Height - child.DesiredSize.Height) / 2), width, child.DesiredSize.Height)); x += width + 8;
        }
        return finalSize;
    }
}

public sealed class RulePolicyLabel : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RulePolicyLabel), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, (target, args) => AutomationProperties.SetName(target, (string)args.NewValue)));
    public static readonly DependencyProperty BrushKeyProperty = DependencyProperty.Register(nameof(BrushKey), typeof(string), typeof(RulePolicyLabel), new FrameworkPropertyMetadata("TextTertiaryBrush", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string BrushKey { get => (string)GetValue(BrushKeyProperty); set => SetValue(BrushKeyProperty, value); }
    public RulePolicyLabel() { Height = 39; ClipToBounds = true; SetResourceReference(FontFamilyProperty, "BodyFont"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); Loaded += (_, _) => DesignTokens.Changed += Refresh; Unloaded += (_, _) => DesignTokens.Changed -= Refresh; }
    private void Refresh() => InvalidateVisual();
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 18) return;
        drawing.DrawEllipse(TryFindResource("SurfaceBrush") as Brush, new Pen(TryFindResource(BrushKey) as Brush ?? Foreground, 2.5), new Point(5, ActualHeight / 2), 3.75, 3.75);
        var text = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 14, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, ActualWidth - 18), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        drawing.DrawText(text, new Point(18, (ActualHeight - text.Height) / 2));
    }
}
