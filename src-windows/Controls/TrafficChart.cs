using System.Windows;
using System.Windows.Media;

namespace Mihomo.Desktop.Controls;

/// <summary>A bounded, native drawing surface. Samples are immutable measurements; redraws never animate numeric readings.</summary>
public sealed class TrafficChart : FrameworkElement
{
    private readonly Queue<(double Up, double Down)> samples = new();
    public TrafficChart() { MinHeight = 170; Height = 190; ClipToBounds = true; }
    public void Add(double upload, double download)
    {
        samples.Enqueue((Math.Max(0, upload), Math.Max(0, download)));
        while (samples.Count > 90) samples.Dequeue();
        InvalidateVisual();
    }
    public void Clear() { samples.Clear(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = ActualWidth; var height = ActualHeight - 10;
        if (width < 2 || height < 2) return;
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.LightGray;
        for (var row = 0; row < 4; row++) context.DrawLine(new Pen(border, 1), new Point(0, height * row / 3), new Point(width, height * row / 3));
        var data = samples.ToArray(); if (data.Length < 2) return;
        var maximum = Math.Max(1024, data.Max(sample => Math.Max(sample.Up, sample.Down)) * 1.1);
        DrawSeries(data.Select(sample => sample.Down).ToArray(), TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue);
        DrawSeries(data.Select(sample => sample.Up).ToArray(), TryFindResource("SuccessBrush") as Brush ?? Brushes.SeaGreen);
        void DrawSeries(double[] values, Brush color)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(0, height - values[0] / maximum * height), false, false);
                for (var i = 1; i < values.Length; i++) path.LineTo(new Point(width * i / (values.Length - 1), height - values[i] / maximum * height), true, false);
            }
            geometry.Freeze(); context.DrawGeometry(null, new Pen(color, 2), geometry);
        }
    }
}
