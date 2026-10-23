using System.Windows;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

/// <summary>The uploaded design's palette, including translucent fills; never flatten a card onto its parent.</summary>
internal static class DesignTokens
{
    internal static event Action? Changed;
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("AppBackgroundBrush", "#F3F3F3", "#202020"),
        ("SolidBaseBrush", "#F3F3F3", "#202020"),
        ("SolidCardBrush", "#FBFBFB", "#2B2B2B"),
        ("LayerBrush", "#7FFFFFFF", "#4C3A3A3A"),
        ("LayerStrokeBrush", "#0F000000", "#33000000"),
        ("CardBrush", "#B2FFFFFF", "#0DFFFFFF"),
        ("CardSecondaryBrush", "#7FF6F6F6", "#08FFFFFF"),
        ("CardHoverBrush", "#9EF9F9F9", "#15FFFFFF"),
        ("CardStrokeBrush", "#0F000000", "#33000000"),
        ("ControlBrush", "#B2FFFFFF", "#0FFFFFFF"),
        ("ControlHoverBrush", "#7FF9F9F9", "#15FFFFFF"),
        ("ControlPressedBrush", "#4CF9F9F9", "#08FFFFFF"),
        ("ControlDisabledBrush", "#4CF9F9F9", "#0BFFFFFF"),
        ("ControlAltBrush", "#06000000", "#1A000000"),
        ("ControlAltHoverBrush", "#0F000000", "#0BFFFFFF"),
        ("ControlStrokeBrush", "#0F000000", "#12FFFFFF"),
        ("ControlElevationBrush", "#29000000", "#12FFFFFF"),
        ("ControlElevationTopBrush", "#0F000000", "#18FFFFFF"),
        ("ControlStrongBrush", "#72000000", "#8BFFFFFF"),
        ("ScrollbarStrongHoverBrush", "#5A5A5A", "#C0C0C1"),
        ("ControlFocusBrush", "#FFFFFF", "#B21E1E1E"),
        ("SubtleHoverBrush", "#0A000000", "#0FFFFFFF"),
        ("SubtlePressedBrush", "#06000000", "#0BFFFFFF"),
        ("DividerBrush", "#14000000", "#15FFFFFF"),
        ("AcrylicBrush", "#F0F9F9F9", "#F52C2C2C"),
        ("AcrylicStrokeBrush", "#0F000000", "#12FFFFFF"),
        ("ToolTipBrush", "#F9F9F9", "#2C2C2C"),
        ("DialogTopBrush", "#FFFFFF", "#2B2B2B"),
        ("DialogBottomBrush", "#F3F3F3", "#202020"),
        ("TextDisabledBrush", "#5C000000", "#5DFFFFFF"),
        ("DisabledTextBrush", "#5C000000", "#5DFFFFFF"),
        ("FocusOuterBrush", "#E4000000", "#FFFFFF"),
        ("FocusInnerBrush", "#FFFFFF", "#B2000000"),
        ("TextPrimaryBrush", "#E4000000", "#FFFFFF"),
        ("TextSecondaryBrush", "#9B000000", "#C8FFFFFF"),
        ("TextTertiaryBrush", "#72000000", "#8BFFFFFF"),
        ("FaintBrush", "#5C000000", "#5DFFFFFF"),
        ("AccentBrush", "#005FB8", "#60CDFF"),
        ("AccentLinkBrush", "#003E92", "#99EBFF"),
        ("AccentTextBrush", "#FFFFFF", "#000000"),
        ("AccentTextSecondaryBrush", "#B2FFFFFF", "#7F000000"),
        ("SuccessBrush", "#0F7B0F", "#6CCB5F"),
        ("WarningBrush", "#9D5D00", "#FCE100"),
        ("DangerBrush", "#C42B1C", "#FF99A4"),
        ("SuccessBackgroundBrush", "#DFF6DD", "#393D1B"),
        ("WarningBackgroundBrush", "#FFF4CE", "#433519"),
        ("CriticalBackgroundBrush", "#FDE7E9", "#442726"),
        ("InfoBackgroundBrush", "#BFF6F6F6", "#08FFFFFF"),
        ("ChartUpBrush", "#8A8A8A", "#9E9E9E"),
        ("ScrimBrush", "#4C000000", "#4C000000"),
        ("SurfaceBrush", "#B2FFFFFF", "#0DFFFFFF"),
        ("SubtleSurfaceBrush", "#7FF6F6F6", "#08FFFFFF"),
        ("BorderBrush", "#0F000000", "#33000000"),
        ("LineSystemBrush", "#9B000000", "#C8FFFFFF"),
        ("OnLineBrush", "#FFFFFF", "#000000"),
        ("Line0Brush", "#0063B1", "#60CDFF"),
        ("Line1Brush", "#6B4FAC", "#C3A6F2"),
        ("Line2Brush", "#B0297A", "#FF8FD0"),
        ("Line3Brush", "#00755E", "#4FD8B4"),
        ("Line4Brush", "#A65200", "#FFB36B"),
        ("Line5Brush", "#1F5BD6", "#82A8FF"),
        ("Line6Brush", "#8A3FC2", "#D6A3FF"),
        ("Line7Brush", "#00727A", "#5DD3D6"),
        ("Line8Brush", "#B33A3A", "#FF9A9A"),
        ("PanelSolidBrush", "#FFFFFF", "#2B2B2B"),
        ("HairStrongBrush", "#72000000", "#8BFFFFFF"),
        ("OnAccentHoverBrush", "#14FFFFFF", "#14FFFFFF"),
        ("OnAccentDividerBrush", "#33FFFFFF", "#33FFFFFF"),
        ("AccentStrokeBrush", "#14FFFFFF", "#14FFFFFF"),
        ("AccentElevationBrush", "#52000000", "#52000000"),
        ("AccentHoverBrush", "#E6005FB8", "#E660CDFF"),
        ("AccentPressedBrush", "#CC005FB8", "#CC60CDFF"),
    ];

    internal static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;
        foreach (var token in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? token.Dark : token.Light));
            brush.Freeze(); resources[token.Key] = brush;
        }
        resources["AccentBrush"] = dark ? SystemColors.AccentColorLight2Brush : SystemColors.AccentColorDark1Brush;
        resources["AccentLinkBrush"] = dark ? SystemColors.AccentColorLight3Brush : SystemColors.AccentColorDark2Brush;
        resources["AccentTextBrush"] = dark ? Brushes.Black : Brushes.White;
        var accent = ((SolidColorBrush)resources["AccentBrush"]).Color;
        resources["AccentHoverBrush"] = new SolidColorBrush(Color.FromArgb((byte)(accent.A * .9), accent.R, accent.G, accent.B));
        resources["AccentPressedBrush"] = new SolidColorBrush(Color.FromArgb((byte)(accent.A * .8), accent.R, accent.G, accent.B));
        if (SystemParameters.HighContrast)
        {
            foreach (var token in Palette)
            {
                bool text = token.Key.Contains("Text") || token.Key.Contains("Stroke") || token.Key.Contains("Strong") || token.Key.Contains("Border") || token.Key.Contains("Divider") || token.Key.Contains("Elevation") || token.Key is "SuccessBrush" or "DangerBrush" or "WarningBrush" or "ChartUpBrush" or "FaintBrush";
                resources[token.Key] = text ? SystemColors.WindowTextBrush : SystemColors.WindowBrush;
            }
            foreach (var key in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentLinkBrush" }) resources[key] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = resources["AccentTextSecondaryBrush"] = SystemColors.HighlightTextBrush;
            resources["FocusOuterBrush"] = SystemColors.WindowTextBrush; resources["FocusInnerBrush"] = SystemColors.WindowBrush;
            for (int i = 0; i < 9; i++) resources["Line" + i + "Brush"] = SystemColors.HighlightBrush;
        }
        Changed?.Invoke();
    }
}
