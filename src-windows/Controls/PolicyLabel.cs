using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

public sealed class PolicyLabel : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(PolicyLabel), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushKeyProperty = DependencyProperty.Register(nameof(BrushKey), typeof(string), typeof(PolicyLabel), new FrameworkPropertyMetadata("LineSystemBrush", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string BrushKey { get => (string)GetValue(BrushKeyProperty); set => SetValue(BrushKeyProperty, value); }
    public PolicyLabel() { Height = 32; ClipToBounds = true; SetResourceReference(FontFamilyProperty, "BodyFont"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); }
    protected override void OnRender(DrawingContext dc)
    {
        var brush = TryFindResource(BrushKey) as Brush ?? Foreground;
        dc.DrawEllipse(brush, null, new Point(4, ActualHeight / 2), 4, 4);
        var text = new FormattedText(Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 14, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, ActualWidth - 16), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(16, (ActualHeight - text.Height) / 2));
    }
}
