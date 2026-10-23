using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>A one-minute, bounded history. Animation changes presentation, never measured values.</summary>
public sealed class TrafficChart : FrameworkElement
{
    private sealed record Sample(double At, DateTimeOffset Time, double Up, double Down, int Segment);
    private readonly List<Sample> samples = [];
    private readonly DispatcherTimer timer;
    private readonly long origin = Stopwatch.GetTimestamp();
    private int segment;
    private double scale = 1228.8;
    private double? hover;
    private bool compact;
    private double Now => Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
    private static bool Animate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    public bool Compact { get => compact; set { compact = value; InvalidateVisual(); } }
    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(nameof(DownBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(nameof(UpBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RuleBrushProperty = DependencyProperty.Register(nameof(RuleBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }
    public Brush RuleBrush { get => (Brush)GetValue(RuleBrushProperty); set => SetValue(RuleBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public TrafficChart()
    {
        Height = 190; ClipToBounds = true; Focusable = true;
        SetResourceReference(DownBrushProperty, "AccentBrush"); SetResourceReference(UpBrushProperty, "ChartUpBrush");
        SetResourceReference(RuleBrushProperty, "BorderBrush"); SetResourceReference(LabelBrushProperty, "TextSecondaryBrush");
        L.Bind(this, AutomationProperties.NameProperty, "最近 60 秒吞吐曲线");
        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => { Prune(); InvalidateVisual(); if (samples.Count == 0) timer.Stop(); };
        Loaded += (_, _) => { L.Changed += LocaleChanged; Resume(); };
        Unloaded += (_, _) => { timer.Stop(); L.Changed -= LocaleChanged; };
        IsVisibleChanged += (_, _) => { if (IsVisible) Resume(); else timer.Stop(); };
        MouseMove += (_, e) => { hover = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { hover = null; InvalidateVisual(); };
        KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Escape)) return;
            hover = e.Key == Key.Escape ? null : Math.Clamp((hover ?? ActualWidth) + (e.Key == Key.Left ? -1 : 1) * ActualWidth / 60, 0, ActualWidth);
            e.Handled = true; InvalidateVisual();
        };
    }
    private void LocaleChanged() => InvalidateVisual();
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);
    private sealed class ChartAutomationPeer(TrafficChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TrafficChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
    private void Resume() { Prune(); timer.Interval = TimeSpan.FromMilliseconds(Animate ? 33 : 1000); if (IsLoaded && IsVisible && samples.Count > 0) timer.Start(); InvalidateVisual(); }
    private void Prune()
    {
        double cutoff = Now - 61000;
        int expired = samples.TakeWhile(sample => sample.At < cutoff).Count();
        int remove = expired == samples.Count ? expired : Math.Max(0, expired - 2);
        if (remove > 0) samples.RemoveRange(0, remove);
    }
    public void Add(double upload, double download)
    {
        if (!double.IsFinite(upload) || !double.IsFinite(download)) return;
        double now = Now;
        if (samples.Count > 0 && now - samples[^1].At > 10000) segment++;
        var sample = new Sample(now, DateTimeOffset.Now, Math.Max(0, upload), Math.Max(0, download), segment);
        if (samples.Count > 0 && Math.Floor(samples[^1].At / 1000) == Math.Floor(now / 1000)) samples[^1] = sample;
        else samples.Add(sample);
        while (samples.Count > 64) samples.RemoveAt(0);
        AutomationProperties.SetHelpText(this, L.F("下载 {0}/s，上传 {1}/s。左右方向键查看历史采样。", Ui.Bytes(download), Ui.Bytes(upload)));
        Resume();
    }
    public void Clear() { samples.Clear(); hover = null; scale = 1228.8; timer.Stop(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = ActualWidth; var height = ActualHeight; if (width < 2 || height < 2) return;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        double top = Compact ? 2 : 16, bottom = Compact ? height - 2 : height - 24, plotHeight = Math.Max(1, bottom - top), now = Now;
        double peak = Math.Max(1024, samples.Where(s => s.At >= now - 61000).Select(s => Compact ? s.Down : Math.Max(s.Up, s.Down)).DefaultIfEmpty().Max()) * 1.2;
        scale = Animate ? scale + (peak - scale) * .10 : peak;
        // Values outside the active minute never influence the inspection tooltip.
        var eligible = samples.Where(s => s.At >= now - 60000 && s.At <= now).ToArray();
        if (!Compact)
        {
            for (int row = 1; row <= 3; row++)
            {
                double y = bottom - plotHeight * row / 3;
                var pen = new Pen(RuleBrush, 1) { DashStyle = DashStyles.Dot }; context.DrawLine(pen, new Point(0, y), new Point(width, y));
                Text(Ui.Bytes(scale * row / 3) + "/s", new Point(4, Math.Max(0, y - 14)), LabelBrush, 10);
            }
            Text(L.T("60 秒前"), new Point(0, bottom + 7), LabelBrush, 10);
            Text(L.T("现在"), new Point(Math.Max(0, width - (L.IsEnglish ? 24 : 26)), bottom + 7), LabelBrush, 10);
        }
        foreach (var group in samples.GroupBy(s => s.Segment))
        {
            var data = group.Where(s => s.At >= now - 62000).ToArray(); if (data.Length == 0) continue;
            DrawSeries(data, false, DownBrush); if (!Compact) DrawSeries(data, true, UpBrush);
        }
        if (!Compact && hover is double hovered && eligible.Length > 0)
        {
            double at = now - 60000 + Math.Clamp(hovered / width, 0, 1) * 60000;
            var selected = eligible.MinBy(s => Math.Abs(s.At - at))!; double x = X(selected.At);
            context.DrawLine(new Pen(LabelBrush, 1) { DashStyle = DashStyles.Dash }, new Point(x, top), new Point(x, bottom));
            context.DrawEllipse(DownBrush, new Pen(TryFindResource("SurfaceBrush") as Brush ?? Brushes.White, 1), new Point(x, Y(selected.Down)), 3.5, 3.5);
            context.DrawEllipse(UpBrush, null, new Point(x, Y(selected.Up)), 3, 3);
            var content = selected.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "\n" + L.F("下载 {0}/s\n上传 {1}/s", Ui.Bytes(selected.Down), Ui.Bytes(selected.Up));
            var label = Format(content, TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black, 12);
            double boxWidth = label.Width + 20, boxHeight = label.Height + 14, left = Math.Clamp(x + 12, 0, Math.Max(0, width - boxWidth));
            context.DrawRoundedRectangle(TryFindResource("SurfaceBrush") as Brush ?? Brushes.White, new Pen(RuleBrush, 1), new Rect(left, top + 4, boxWidth, boxHeight), 6, 6);
            context.DrawText(label, new Point(left + 10, top + 11));
        }
        double X(double time) => width * (1 - (now - time) / 60000);
        double Y(double value) => bottom - Math.Clamp(value / Math.Max(1, scale), 0, 1) * plotHeight;
        void DrawSeries(Sample[] data, bool up, Brush brush)
        {
            double Value(Sample s) => up ? s.Up : s.Down;
            var points = data.Select(s => new Point(X(s.At), Y(Value(s)))).ToList();
            var latest = data[^1]; double age = now - latest.At;
            bool tail = age >= 0 && age < 2000 && ReferenceEquals(latest, samples.LastOrDefault());
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++)
                {
                    var a = points[i - 1]; var b = points[i]; var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    path.QuadraticBezierTo(a, mid, true, false);
                }
                if (points.Count > 1) path.LineTo(points[^1], true, false);
            }
            geometry.Freeze(); context.DrawGeometry(null, new Pen(brush, Compact ? 1.5 : 2), geometry);
            if (!up && points.Count > 1)
            {
                var fill = new StreamGeometry(); using (var path = fill.Open())
                {
                    path.BeginFigure(new Point(points[0].X, bottom), true, true); path.LineTo(points[0], true, false);
                    for (int i = 1; i < points.Count; i++) { var a = points[i - 1]; var b = points[i]; path.QuadraticBezierTo(a, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), true, false); }
                    path.LineTo(points[^1], true, false); path.LineTo(new Point(points[^1].X, bottom), true, false);
                }
                fill.Freeze(); context.PushOpacity(.07); context.DrawGeometry(brush, null, fill); context.Pop();
            }
            if (tail) { context.PushOpacity(Math.Min(1, (2000 - age) / 500)); context.DrawLine(new Pen(brush, Compact ? 1.5 : 2), points[^1], new Point(width, points[^1].Y)); if (!Compact) context.DrawEllipse(brush, null, new Point(width - 2, points[^1].Y), 2.5, 2.5); context.Pop(); }
        }
        FormattedText Format(string value, Brush brush, double size) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        void Text(string value, Point point, Brush brush, double size) => context.DrawText(Format(value, brush, size), point);
    }
}
