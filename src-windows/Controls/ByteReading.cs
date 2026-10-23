using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

/// <summary>The supplied Flux/StaticBytes typography and measured-rate formatting.</summary>
internal static class ByteReading
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];
    private static (string Number, string Unit) Parts(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 1) return ("0", "B");
        int exponent = Math.Min(Units.Length - 1, (int)Math.Floor(Math.Log(bytes) / Math.Log(1024)));
        double value = bytes / Math.Pow(1024, exponent);
        int digits = exponent == 0 ? 0 : value < 10 ? 2 : value < 100 ? 1 : 0;
        return (value.ToString("F" + digits, CultureInfo.InvariantCulture), Units[exponent]);
    }
    internal static string Rate(double value)
    {
        var (number, unit) = Parts(value); return number + " " + unit + "/s";
    }
    internal static void SetRate(TextBlock target, double value)
    {
        var (number, unit) = Parts(value);
        target.Inlines.Clear(); target.Inlines.Add(new Run(number));
        var label = new TextBlock { Text = unit + "/s", FontFamily = target.FontFamily, FontSize = target.FontSize * .42, FontWeight = FontWeights.Medium, Opacity = .6, Margin = new Thickness(target.FontSize * .42 * .3, 0, 0, 0) };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(TextBlock.Foreground)) { Source = target });
        target.Inlines.Add(new InlineUIContainer(label) { BaselineAlignment = BaselineAlignment.Baseline });
    }
}
