using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The DNS range chart: local-clock averages, monotone cubic paths and measured-data readouts.</summary>
public sealed class DnsTrendChart : FrameworkElement
{
    private const double PlotHeight = 228, LeftPad = 46, RightPad = 16, TopPad = 14, Base = 198;
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly DispatcherTimer timer;
    private readonly Canvas readoutShadow = new() { Background = Brushes.Transparent };
    private readonly Border shadowShape = new() { CornerRadius = new CornerRadius(4) };
    private DnsTrendPoint[] samples = [];
    private Point[][] target = [[], [], []], previous = [[], [], []];
    private double[] ticks = [0, 1];
    private double morphStarted = double.NegativeInfinity, entryStarted = double.NegativeInfinity, cursorStarted = double.NegativeInfinity;
    private double geometryWidth;
    private Point[] cursorFrom = [], cursorTarget = [];
    private int range = 24;
    private int? hover;
    private double Now => Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
    private bool Narrow => (Window.GetWindow(this)?.ActualWidth ?? ActualWidth) <= 768;
    public bool HasPoints => samples.Length > 0;
    public bool EmptyRange => samples.All(sample => sample.Queries == 0 && sample.Cache == 0 && sample.Errors == 0);
    public DnsTrendChart()
    {
        Height = PlotHeight; Focusable = true; Cursor = Cursors.Cross; ClipToBounds = false;
        readoutShadow.Children.Add(shadowShape); Canvas.SetLeft(shadowShape, 24); Canvas.SetTop(shadowShape, 24);
        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000d / 60) };
        timer.Tick += (_, _) => { timer.Stop(); InvalidateVisual(); };
        Loaded += (_, _) => { L.Changed += LocaleChanged; DesignTokens.Changed += InvalidateVisual; entryStarted = Now; Rebuild(false); InvalidateVisual(); };
        Unloaded += (_, _) => { timer.Stop(); L.Changed -= LocaleChanged; DesignTokens.Changed -= InvalidateVisual; };
        IsVisibleChanged += (_, _) => { timer.Stop(); if (IsVisible) InvalidateVisual(); };
        SizeChanged += (_, _) => { if (Math.Abs(ActualWidth - geometryWidth) > .1) Rebuild(true); };
        MouseMove += (_, e) => Track(e.GetPosition(this).X);
        MouseLeftButtonDown += (_, e) => { Focus(); Track(e.GetPosition(this).X); e.Handled = true; };
        MouseLeave += (_, _) => SetHover(null);
        GotKeyboardFocus += (_, _) => { if (hover is null && samples.Length > 0) SetHover(samples.Length - 1); };
        LostKeyboardFocus += (_, _) => SetHover(null);
        KeyDown += (_, e) =>
        {
            if (samples.Length == 0) return;
            int current = hover ?? samples.Length - 1;
            int? next = e.Key switch { Key.Left => Math.Max(0, current - 1), Key.Right => Math.Min(samples.Length - 1, current + 1), Key.Home => 0, Key.End => samples.Length - 1, _ => null };
            if (e.Key == Key.Escape) SetHover(null);
            else if (next.HasValue) { SetHover(next); e.Handled = true; }
        };
        LocaleChanged();
    }
    private void LocaleChanged() { AutomationProperties.SetName(this, L.F("DNS 查询趋势 · {0}", L.T(range == 1 ? "1 小时" : range == 6 ? "6 小时" : "24 小时"))); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);
    private sealed class ChartPeer(DnsTrendChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(DnsTrendChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
    public void SetSeries(JsonElement series, int hours)
    {
        var next = DnsTrendData.Build(series, hours, hours == 1 ? 1 : hours == 6 ? 5 : 10);
        bool changedRange = range != hours, wasEmpty = samples.Length == 0;
        if (!changedRange && samples.SequenceEqual(next)) { LocaleChanged(); return; }
        samples = next; range = hours;
        if (changedRange || wasEmpty) { entryStarted = Now; hover = null; }
        if (hover is int selected) hover = Math.Min(selected, samples.Length - 1);
        if (samples.Length == 0) hover = null;
        Rebuild(!changedRange && !wasEmpty); LocaleChanged();
    }
    private void Rebuild(bool morph)
    {
        double now = Now; previous = ShownPaths(now); geometryWidth = ActualWidth;
        ticks = DnsTrendData.NiceTicks(samples.Select(sample => Math.Max(sample.Queries, Math.Max(sample.Cache, sample.Errors))).DefaultIfEmpty().Max());
        target = Enumerable.Range(0, 3).Select(series => DnsTrendData.Monotone(samples.Select(sample => new Point(X(sample.Time), Y(Value(sample, series)))).ToArray())).ToArray();
        if (!morph || !FluentMotion.Enabled) previous = target;
        morphStarted = now;
        if (hover.HasValue) RetargetCursor(now);
        UpdateHeight(); InvalidateVisual();
    }
    private double X(double time) => samples.Length == 1 ? LeftPad + Math.Max(1, ActualWidth - LeftPad - RightPad) / 2
        : LeftPad + (time - (samples.Length > 0 ? samples[0].Time : 0)) / Math.Max(1, samples.Length > 0 ? samples[^1].Time - samples[0].Time : 1) * Math.Max(1, ActualWidth - LeftPad - RightPad);
    private double Y(double value) => Base - value / ticks[^1] * (Base - TopPad);
    private static double Value(DnsTrendPoint point, int series) => series == 0 ? point.Cache : series == 1 ? point.Errors : point.Queries;
    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    private Point[][] ShownPaths(double now)
    {
        double t = FluentMotion.Enabled ? FluentMotion.Decelerate.Ease(Math.Clamp((now - morphStarted) / 600, 0, 1)) : 1;
        return target.Select((points, series) => t < 1 && previous[series].Length == points.Length ? points.Select((point, index) => Lerp(previous[series][index], point, t)).ToArray() : points).ToArray();
    }
    private void Track(double x)
    {
        if (samples.Length == 0) return;
        double time = samples[0].Time + (x - LeftPad) / Math.Max(1, ActualWidth - LeftPad - RightPad) * Math.Max(1, samples[^1].Time - samples[0].Time);
        int low = 0, high = samples.Length - 1;
        while (low < high) { int middle = (low + high) >> 1; if (samples[middle].Time < time) low = middle + 1; else high = middle; }
        SetHover(low > 0 && Math.Abs(samples[low - 1].Time - time) <= Math.Abs(samples[low].Time - time) ? low - 1 : low);
    }
    private void SetHover(int? value)
    {
        if (hover == value) return;
        bool entering = hover is null; hover = value;
        if (hover is int index)
        {
            RetargetCursor(Now); if (entering) cursorFrom = cursorTarget;
            var sample = samples[index]; AutomationProperties.SetHelpText(this, L.F("{0} · 查询量 {1} · 缓存命中 {2} · 错误 {3}", TimeLabel(sample), PerMinute(sample.Queries), PerMinute(sample.Cache), PerMinute(sample.Errors)));
        }
        UpdateHeight(); InvalidateVisual();
    }
    private Point[] ShownCursor(double now)
    {
        double t = FluentMotion.Enabled ? FluentMotion.Decelerate.Ease(Math.Clamp((now - cursorStarted) / 120, 0, 1)) : 1;
        return cursorFrom.Length == cursorTarget.Length && t < 1 ? cursorTarget.Select((point, index) => Lerp(cursorFrom[index], point, t)).ToArray() : cursorTarget;
    }
    private void RetargetCursor(double now)
    {
        cursorFrom = ShownCursor(now); cursorStarted = now;
        cursorTarget = hover is int index && index >= 0 && index < samples.Length ? Enumerable.Range(0, 3).Select(series => new Point(X(samples[index].Time), Y(Value(samples[index], series)))).ToArray() : [];
    }
    private void UpdateHeight()
    {
        double height = PlotHeight;
        if (Narrow && hover is int index && index >= 0 && index < samples.Length) height += 8 + NarrowReadout(samples[index], null);
        if (Height != height) Height = height;
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context); timer.Stop();
        if (ActualWidth < LeftPad + RightPad || samples.Length == 0) return;
        double now = Now, width = ActualWidth;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, PlotHeight));
        var hair = Resource("DividerBrush"); var strongHair = Resource("ControlStrongBrush");
        foreach (double tick in ticks)
        {
            double y = Y(tick); context.DrawLine(new Pen(tick == 0 ? strongHair : hair, 1), new Point(LeftPad, y), new Point(width - RightPad, y));
            var text = Text(PerMinute(tick), "TextTertiaryBrush", 10.5, true); context.DrawText(text, new Point(LeftPad - 10 - text.Width, y + 10.5 * .32 - text.Baseline));
        }
        foreach (double tick in DnsTrendData.TimeTicks(samples[0].Time, samples[^1].Time, width - LeftPad - RightPad))
        {
            double x = X(tick); var text = Text(Clock(tick), "TextTertiaryBrush", 10.5, true);
            context.DrawLine(new Pen(strongHair, 1), new Point(x, Base), new Point(x, Base + 4));
            double left = x - LeftPad < 22 ? x : width - RightPad - x < 22 ? x - text.Width : x - text.Width / 2;
            context.DrawText(text, new Point(left, Base + 18 - text.Baseline));
        }
        var paths = ShownPaths(now); var brushes = new[] { Resource("ChartUpBrush"), Resource("DangerBrush"), Resource("AccentBrush") };
        double fillProgress = Progress(now - entryStarted - 260, 700);
        if (paths[2].Length > 0 && brushes[2] is SolidColorBrush accent)
        {
            var color = accent.Color; context.PushOpacity(fillProgress);
            var fill = new LinearGradientBrush(Color.FromArgb((byte)Math.Round(color.A * .12), color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), new Point(0, 0), new Point(0, 1));
            context.DrawGeometry(fill, null, Geometry(paths[2], true)); context.Pop();
        }
        for (int series = 0; series < 3; series++)
        {
            var points = paths[series]; if (points.Length == 0) continue;
            double stroke = series == 1 ? 1.6 : 2, progress = Progress(now - entryStarted - series * 80, 900);
            var geometry = Geometry(points, false); var pen = new Pen(brushes[series], stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round, DashCap = PenLineCap.Round };
            if (progress < 1)
            {
                double length = Math.Max(.1, DnsTrendData.Length(points)); pen.DashStyle = new DashStyle([length / stroke, length / stroke], (1 - progress) * length / stroke);
            }
            context.DrawGeometry(null, pen, geometry);
        }
        if (hover is int index && index >= 0 && index < samples.Length)
        {
            var points = ShownCursor(now);
            if (points.Length == 3)
            {
                context.DrawLine(new Pen(strongHair, 1), new Point(points[0].X, TopPad), new Point(points[0].X, Base));
                for (int series = 0; series < 3; series++) context.DrawEllipse(brushes[series], new Pen(Resource("DialogTopBrush"), 2), points[series], 4, 4);
                if (Narrow) NarrowReadout(samples[index], context); else Readout(context, samples[index], points[0].X);
            }
        }
        if (FluentMotion.Enabled && IsLoaded && IsVisible && (now - entryStarted < 1060 || now - morphStarted < 600 || hover.HasValue && now - cursorStarted < 120)) timer.Start();
    }
    private static double Progress(double elapsed, double duration) => FluentMotion.Enabled ? FluentMotion.Decelerate.Ease(Math.Clamp(elapsed / duration, 0, 1)) : 1;
    private static StreamGeometry Geometry(Point[] points, bool area)
    {
        var geometry = new StreamGeometry(); if (points.Length == 0) return geometry;
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], area, area);
            if (points.Length == 1) path.LineTo(new Point(points[0].X + .1, points[0].Y), true, false);
            for (int index = 1; index + 2 < points.Length; index += 3) path.BezierTo(points[index], points[index + 1], points[index + 2], true, true);
            if (area) { path.LineTo(new Point(points[^1].X, Base), true, false); path.LineTo(new Point(points[0].X, Base), true, false); }
        }
        geometry.Freeze(); return geometry;
    }
    private void Readout(DrawingContext context, DnsTrendPoint sample, double x)
    {
        var entries = Readings(sample); var time = Text(TimeLabel(sample), "TextTertiaryBrush", 12, true);
        double width = Math.Max(156, Math.Max(time.Width, entries.Max(entry => entry.Label.Width + entry.Value.Width + (entry.Key is null ? 12 : 28))) + 22);
        double height = 18 + 19.16 + entries.Length * 17.16 + entries.Length * 3 + (sample.Minutes > 1 ? 8 : 0);
        double left = x > ActualWidth * .62 ? x - width - 14 : x + 14;
        left = Math.Clamp(left, 0, Math.Max(0, ActualWidth - width));
        DrawReadoutFrame(context, new Rect(left, TopPad, width, height));
        context.DrawText(time, new Point(left + 11, TopPad + 9 + (17.16 - time.Height) / 2)); double y = TopPad + 31.16;
        foreach (var entry in entries)
        {
            if (entry.Key is null) { context.DrawLine(new Pen(Resource("DividerBrush"), 1), new Point(left + 11, y + 2), new Point(left + width - 11, y + 2)); y += 8; }
            else context.DrawRoundedRectangle(Resource(entry.Key), null, new Rect(left + 11, y + 7.58, 10, 2), 1, 1);
            context.DrawText(entry.Label, new Point(left + 11 + (entry.Key is null ? 0 : 16), y + (17.16 - entry.Label.Height) / 2)); context.DrawText(entry.Value, new Point(left + width - 11 - entry.Value.Width, y + (17.16 - entry.Value.Height) / 2)); y += 20.16;
        }
    }
    private double NarrowReadout(DnsTrendPoint sample, DrawingContext? context)
    {
        double width = Math.Max(1, ActualWidth), available = Math.Max(1, width - 22), x = 0, y = 0;
        var time = Text(TimeLabel(sample), "TextTertiaryBrush", 12, true);
        var positions = new List<(double X, double Y, FormattedText Label, FormattedText? Value, string? Key)>();
        positions.Add((0, 0, time, null, null)); y = 21.16;
        foreach (var entry in Readings(sample))
        {
            double extent = entry.Label.Width + entry.Value.Width + 12 + (entry.Key is null ? 0 : 16);
            if (x > 0 && x + extent > available) { x = 0; y += 21.16; }
            positions.Add((x, y, entry.Label, entry.Value, entry.Key)); x += extent + 14;
        }
        double height = y + 35.16;
        if (context is null) return height;
        double top = PlotHeight + 8;
        DrawReadoutFrame(context, new Rect(0, top, width, height));
        foreach (var entry in positions)
        {
            double left = 11 + entry.X, line = top + 9 + entry.Y;
            if (entry.Key is not null) context.DrawRoundedRectangle(Resource(entry.Key), null, new Rect(left, line + 7.58, 10, 2), 1, 1);
            double labelX = left + (entry.Key is null ? 0 : 16); context.DrawText(entry.Label, new Point(labelX, line + (17.16 - entry.Label.Height) / 2));
            if (entry.Value is not null) context.DrawText(entry.Value, new Point(labelX + entry.Label.Width + 12, line + (17.16 - entry.Value.Height) / 2));
        }
        return height;
    }
    private void DrawReadoutFrame(DrawingContext context, Rect bounds)
    {
        var surface = Resource("DialogTopBrush"); bool dark = surface is SolidColorBrush color && color.Color.R < 128;
        shadowShape.Background = surface; shadowShape.Width = bounds.Width; shadowShape.Height = bounds.Height;
        if (shadowShape.Effect is not DropShadowEffect effect) { effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 8, Direction = 270, RenderingBias = RenderingBias.Quality }; shadowShape.Effect = effect; }
        effect.Opacity = dark ? .26 : .14;
        readoutShadow.Width = bounds.Width + 48; readoutShadow.Height = bounds.Height + 48;
        readoutShadow.Measure(new Size(readoutShadow.Width, readoutShadow.Height)); readoutShadow.Arrange(new Rect(0, 0, readoutShadow.Width, readoutShadow.Height));
        context.DrawRectangle(new VisualBrush(readoutShadow), null, new Rect(bounds.X - 24, bounds.Y - 24, readoutShadow.Width, readoutShadow.Height));
        context.DrawRoundedRectangle(surface, new Pen(Resource("DividerBrush"), 1), bounds, 4, 4);
    }
    private (FormattedText Label, FormattedText Value, string? Key)[] Readings(DnsTrendPoint sample)
    {
        var entries = new List<(FormattedText, FormattedText, string?)>();
        foreach (var (label, value, key) in new[] { ("查询量", sample.Queries, "AccentBrush"), ("缓存命中", sample.Cache, "ChartUpBrush"), ("错误", sample.Errors, "DangerBrush") })
            entries.Add((Text(L.T(label), "TextSecondaryBrush", 12), Text(PerMinute(value), "TextPrimaryBrush", 12, true, true), key));
        if (sample.Minutes > 1) entries.Add((Text(L.T("区间查询总数"), "TextSecondaryBrush", 12), Text(DnsVisuals.Number(sample.TotalQueries), "TextPrimaryBrush", 12, true, true), null)); return entries.ToArray();
    }
    private Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    private FormattedText Text(string value, string brush, double size, bool mono = false, bool strong = false) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(mono ? DnsVisuals.Mono : TryFindResource("BodyFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, strong ? FontWeights.Medium : FontWeights.Normal, FontStretches.Normal), size, Resource(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private static string PerMinute(double value) => DnsVisuals.Number(value, value > 0 && value < 10 ? 1 : 0);
    private static string Clock(double milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds).ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
    private static string TimeLabel(DnsTrendPoint sample) => sample.Minutes <= 1 && sample.End - sample.Start <= 60000 ? Clock(sample.Start) : Clock(sample.Start) + " – " + Clock(sample.End);
}

internal sealed record DnsTrendPoint(double Start, double End, double Time, int Minutes, double Queries, double Cache, double Errors, double TotalQueries);

/// <summary>Literal data/curve rules shared with the original DNS/trend.ts, without invented minute buckets.</summary>
internal static class DnsTrendData
{
    internal static DnsTrendPoint[] Build(JsonElement series, int hours, int size)
    {
        var minutes = series.Items().Select(item => DateTimeOffset.TryParse(item.Text("time"), out var time) ? (Time: time.ToUnixTimeMilliseconds(), Item: item, Valid: true) : default).Where(item => item.Valid).ToList();
        if (minutes.Count > 1) minutes.RemoveAt(minutes.Count - 1);
        if (minutes.Count == 0) return [];
        double from = minutes[^1].Time - (hours * 60 - 1) * 60000d; size = Math.Max(1, size);
        var points = new List<DnsTrendPoint>(); var current = new List<(long Time, JsonElement Item, bool Valid)>(); double? key = null;
        foreach (var minute in minutes)
        {
            if (minute.Time < from) continue;
            double next = Math.Floor((Math.Floor(minute.Time / 60000d) + TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(minute.Time)).TotalMinutes) / size);
            if (key != next) { Flush(); key = next; } current.Add(minute);
        }
        Flush(); return points.ToArray();
        void Flush()
        {
            if (current.Count == 0) return;
            double queries = current.Sum(item => item.Item.Number("queries"));
            points.Add(new DnsTrendPoint(current[0].Time, current[^1].Time + 60000, current.Average(item => item.Time + 30000d), current.Count,
                queries / current.Count, current.Average(item => item.Item.Number("cache_hits")), current.Average(item => item.Item.Number("errors")), queries)); current.Clear();
        }
    }
    internal static double[] NiceTicks(double maximum)
    {
        if (!(maximum > 0)) return [0, 1];
        double raw = maximum / 4, power = Math.Pow(10, Math.Floor(Math.Log10(raw))), fraction = raw / power;
        double step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10) * power;
        double top = Math.Ceiling(maximum / step - 1e-9) * step;
        return Enumerable.Range(0, (int)Math.Round(top / step) + 1).Select(index => index * step).ToArray();
    }
    internal static IEnumerable<double> TimeTicks(double from, double to, double width)
    {
        double span = to - from; if (!(span > 0) || !(width > 0)) yield break;
        int step = new[] { 5, 10, 15, 30, 60, 120, 180, 240, 360, 720 }.FirstOrDefault(minutes => width / (span / (minutes * 60000d)) >= 64, 720);
        double offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds((long)from)).TotalMinutes;
        double local = Math.Ceiling((Math.Ceiling(from / 60000) + offset) / step) * step;
        for (double time = (local - offset) * 60000; time <= to; time += step * 60000d) yield return time;
    }
    internal static Point[] Monotone(Point[] points)
    {
        if (points.Length <= 1) return points;
        int count = points.Length; var dx = new double[count - 1]; var slopes = new double[count - 1]; var tangent = new double[count];
        for (int index = 0; index < count - 1; index++) { dx[index] = points[index + 1].X - points[index].X; slopes[index] = dx[index] == 0 ? 0 : (points[index + 1].Y - points[index].Y) / dx[index]; }
        tangent[0] = slopes[0]; tangent[^1] = slopes[^1];
        for (int index = 1; index < count - 1; index++)
        {
            if (slopes[index - 1] * slopes[index] <= 0) tangent[index] = 0;
            else { double w1 = 2 * dx[index] + dx[index - 1], w2 = dx[index] + 2 * dx[index - 1]; tangent[index] = (w1 + w2) / (w1 / slopes[index - 1] + w2 / slopes[index]); }
        }
        var path = new List<Point> { Round(points[0]) };
        for (int index = 0; index < count - 1; index++)
        {
            double h = dx[index] / 3; path.Add(Round(new Point(points[index].X + h, points[index].Y + tangent[index] * h)));
            path.Add(Round(new Point(points[index + 1].X - h, points[index + 1].Y - tangent[index + 1] * h))); path.Add(Round(points[index + 1]));
        }
        return path.ToArray();
        static Point Round(Point point) => new(Math.Round(point.X, 1), Math.Round(point.Y, 1));
    }
    internal static double Length(Point[] path)
    {
        if (path.Length <= 1) return .1;
        double length = 0; Point previous = path[0];
        for (int index = 1; index + 2 < path.Length; index += 3)
        {
            Point start = path[index - 1], a = path[index], b = path[index + 1], end = path[index + 2];
            for (int sample = 1; sample <= 16; sample++)
            {
                double t = sample / 16d, u = 1 - t;
                var point = new Point(u * u * u * start.X + 3 * u * u * t * a.X + 3 * u * t * t * b.X + t * t * t * end.X,
                    u * u * u * start.Y + 3 * u * u * t * a.Y + 3 * u * t * t * b.Y + t * t * t * end.Y);
                length += (point - previous).Length; previous = point;
            }
        }
        return length;
    }
}
