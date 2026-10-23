using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

public sealed class DnsTrendChart : FrameworkElement
{
    private Sample[] samples = [];
    private sealed record Sample(DateTimeOffset Time, double Queries, double Cache, double Errors);
    public DnsTrendChart()
    {
        Height = 200; MinHeight = 160; ClipToBounds = true; MouseMove += Inspect;
        L.Bind(this, AutomationProperties.NameProperty, "查询趋势");
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);
    private sealed class ChartPeer(DnsTrendChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(DnsTrendChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
    public void SetSeries(JsonElement series, int hours)
    {
        var all = series.Items().Select(item => DateTimeOffset.TryParse(item.Text("time"), out var time) ? new Sample(time, item.Number("queries"), item.Number("cache_hits"), item.Number("errors")) : null)
            .OfType<Sample>().OrderBy(item => item.Time).ToArray();
        // The current minute is still filling. Display only completed buckets, like the web dashboard.
        if (all.Length > 0) all = all[..^1];
        var since = all.Length > 0 ? all[^1].Time.AddHours(-hours) : DateTimeOffset.MinValue;
        samples = all.Where(item => item.Time >= since).ToArray(); InvalidateVisual();
    }
    private void Inspect(object sender, MouseEventArgs e)
    {
        if (samples.Length == 0) return;
        var point = e.GetPosition(this); var index = (int)Math.Round(Math.Clamp((point.X - 42) / Math.Max(1, ActualWidth - 54), 0, 1) * (samples.Length - 1));
        var item = samples[index]; ToolTip = L.F("{0:MM-dd HH:mm}\n查询 {1:N0} / 分钟\n缓存命中 {2:N0} / 分钟\n错误 {3:N0} / 分钟", item.Time.ToLocalTime(), item.Queries, item.Cache, item.Errors);
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context); var width = ActualWidth - 54; var height = ActualHeight - 28; if (width < 1 || height < 1) return;
        var text = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        var border = TryFindResource("BorderBrush") as Brush ?? Brushes.LightGray;
        var max = Math.Max(1, samples.Select(item => item.Queries).DefaultIfEmpty().Max());
        for (var tick = 0; tick <= 3; tick++)
        {
            var y = height * tick / 3; context.DrawLine(new Pen(border, 1), new Point(42, y), new Point(ActualWidth - 12, y));
            DrawText((max * (3 - tick) / 3).ToString("0.#", CultureInfo.CurrentCulture), new Point(0, Math.Max(0, y - 7)));
        }
        if (samples.Length < 2) { DrawText(L.T("等待完整分钟的查询数据"), new Point(48, height / 2)); return; }
        Draw(item => item.Queries, TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue);
        Draw(item => item.Cache, TryFindResource("ChartUpBrush") as Brush ?? Brushes.Gray);
        Draw(item => item.Errors, TryFindResource("DangerBrush") as Brush ?? Brushes.Firebrick);
        DrawText(samples[0].Time.ToLocalTime().ToString("HH:mm"), new Point(42, height + 6));
        DrawText(samples[^1].Time.ToLocalTime().ToString("HH:mm"), new Point(Math.Max(42, ActualWidth - 45), height + 6));
        void Draw(Func<Sample, double> field, Brush color)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(42, height * (1 - field(samples[0]) / max)), false, false);
                for (var i = 1; i < samples.Length; i++) path.LineTo(new Point(42 + width * i / (samples.Length - 1), height * (1 - field(samples[i]) / max)), true, false);
            }
            geometry.Freeze(); context.DrawGeometry(null, new Pen(color, 1.5), geometry);
        }
        void DrawText(string value, Point point) => context.DrawText(new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    }
}
