using System.Diagnostics;
using System.Globalization;
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

/// <summary>Measured one-minute throughput, and the original forty-reading tray sparkline.</summary>
public sealed class TrafficChart : FrameworkElement
{
    private readonly List<RateSample> samples = [];
    private readonly ThroughputMotion motion = new();
    private readonly DispatcherTimer timer;
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly Image[] glow = [new(), new()];
    private int segment;
    private double? lastSampleAt;
    private double? hover;
    private bool compact;
    private Point[] sparkFrom = [new(0, 26), new(100, 26)];
    private Point[] sparkTarget = [new(0, 26), new(100, 26)];
    private double sparkStarted = double.NegativeInfinity;
    private double Now => Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
    private static bool Animate => FluentMotion.Enabled;
    public bool Compact { get => compact; set { compact = value; Focusable = !value; ClipToBounds = !value; InvalidateVisual(); } }
    /// <summary>Home omits the right-hand caption; the connections chart keeps it.</summary>
    public bool ShowNowLabel { get; set; } = true;
    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(nameof(DownBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(nameof(UpBrush), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
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
        SetResourceReference(RuleBrushProperty, "DividerBrush"); SetResourceReference(LabelBrushProperty, "TextDisabledBrush");
        L.Bind(this, AutomationProperties.NameProperty, "最近 60 秒吞吐曲线");
        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
        timer.Tick += (_, _) => { timer.Stop(); Prune(); InvalidateVisual(); };
        Loaded += (_, _) => { L.Changed += LocaleChanged; Prune(); InvalidateVisual(); };
        Unloaded += (_, _) => { timer.Stop(); L.Changed -= LocaleChanged; };
        IsVisibleChanged += (_, _) => { timer.Stop(); if (IsVisible) { Prune(); InvalidateVisual(); } };
        MouseMove += (_, e) => { if (Compact) return; hover = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { hover = null; InvalidateVisual(); };
        KeyDown += (_, e) =>
        {
            if (Compact || e.Key is not (Key.Left or Key.Right or Key.Escape)) return;
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
    private void Schedule(double? milliseconds)
    {
        timer.Stop();
        if (milliseconds is null || !IsLoaded || !IsVisible) return;
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds.Value)); timer.Start();
    }
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
        double now = Now, elapsed = lastSampleAt is null ? 1000 : Math.Max(1, now - lastSampleAt.Value);
        if (elapsed > 10000) segment++;
        lastSampleAt = now;
        var sample = new RateSample(now, Math.Max(0, upload), Math.Max(0, download), segment, elapsed);
        if (samples.Count > 0 && samples[^1].Segment == segment && Math.Floor(samples[^1].At / 1000) == Math.Floor(now / 1000))
        {
            var previous = samples[^1]; double span = previous.Span + elapsed;
            samples[^1] = sample with { Up = (previous.Up * previous.Span + sample.Up * elapsed) / span, Down = (previous.Down * previous.Span + sample.Down * elapsed) / span, Span = span };
        }
        else samples.Add(sample);
        while (samples.Count > 64) samples.RemoveAt(0);
        Prune();
        if (Compact) RetargetSpark(now);
        AutomationProperties.SetHelpText(this, L.F("下载 {0}/s，上传 {1}/s。左右方向键查看历史采样。", Ui.Bytes(download), Ui.Bytes(upload)));
        InvalidateVisual();
    }
    public void Clear()
    {
        samples.Clear(); lastSampleAt = hover = null; motion.Reset(); timer.Stop();
        sparkFrom = sparkTarget = [new Point(0, 26), new Point(100, 26)]; sparkStarted = double.NegativeInfinity;
        InvalidateVisual();
    }
    internal object CaptureReadings()
    {
        double now = Now;
        return new { samples = samples.Select(sample => new { ageMilliseconds = now - sample.At, up = sample.Up, down = sample.Down, spanMilliseconds = sample.Span, segment = sample.Segment }).ToArray(), up = samples.Count > 0 ? samples[^1].Up : 0, down = samples.Count > 0 ? samples[^1].Down : 0 };
    }
    private Point[] ShownSpark(double now)
    {
        double progress = Animate ? FluentMotion.Decelerate.Ease(Math.Clamp((now - sparkStarted) / 900, 0, 1)) : 1;
        return progress >= 1 || sparkFrom.Length != sparkTarget.Length ? sparkTarget : sparkTarget.Select((point, index) => new Point(
            sparkFrom[index].X + (point.X - sparkFrom[index].X) * progress, sparkFrom[index].Y + (point.Y - sparkFrom[index].Y) * progress)).ToArray();
    }
    private void RetargetSpark(double now)
    {
        var values = samples.TakeLast(40).Select(sample => sample.Down).ToArray();
        if (values.Length < 2) values = [0, 0];
        double maximum = Math.Max(1, values.Max()) * 1.12;
        var target = values.Select((value, index) => new Point(index * 100d / (values.Length - 1), 26 - Math.Clamp(value / maximum, 0, 1) * 24)).ToArray();
        var shown = ShownSpark(now);
        // CSS path interpolation applies only while the path command counts
        // match. New observations are never fabricated to fill the sparkline.
        sparkFrom = Animate && shown.Length == target.Length ? shown : target; sparkTarget = target; sparkStarted = now;
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double width = ActualWidth, height = ActualHeight, now = Now;
        if (width < 2 || height < 2) { Schedule(null); return; }
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (Compact)
        {
            DrawSpark(context, width, height, now);
            Schedule(Animate && now - sparkStarted < 900 ? 1000d / 60 : null);
            return;
        }
        bool small = height < 120;
        double top = small ? 6 : 16, bottom = height - (small ? 4 : 20), plotHeight = Math.Max(1, bottom - top), plotWidth = Math.Max(0, width - 3);
        Prune(); var frame = motion.Frame(samples, now, !Animate);
        int rows = small ? 2 : 3;
        for (int row = 1; row <= rows; row++)
        {
            double y = Math.Round(bottom - plotHeight * row / rows) + .5;
            context.DrawLine(new Pen(RuleBrush, 1) { DashStyle = new DashStyle([2d, 4d], 0) }, new Point(0, y), new Point(width, y));
            if (!small)
            {
                var label = Format(ByteReading.Rate(frame.Maximum * row / rows), LabelBrush, 11);
                context.DrawText(label, new Point(4, y - 4 - label.Height));
            }
        }
        if (!small)
        {
            foreach (int seconds in new[] { 60, 45, 30, 15 })
            {
                var label = Format("-" + seconds + "s", LabelBrush, 11);
                context.DrawText(label, new Point(plotWidth * (1 - seconds / 60d) + 4, height - 5 - label.Baseline));
            }
            if (ShowNowLabel)
            {
                var label = Format(L.T("现在"), LabelBrush, 11); context.DrawText(label, new Point(width - 4 - label.Width, height - 5 - label.Baseline));
            }
        }
        double X(double at) => plotWidth * (1 - (frame.EndAt - at) / 60000);
        double Y(double value) => bottom - Math.Clamp(value / frame.Maximum, 0, 1) * plotHeight;
        if (frame.Paths.Count > 0 || frame.Tail is not null)
        {
            context.PushClip(new RectangleGeometry(new Rect(0, top, width, plotHeight)));
            DrawSeries(false, DownBrush, 2.2, .34, 0); DrawSeries(true, UpBrush, 1.8, .16, 1);
            context.Pop();
        }
        if (hover is double hovered && frame.Samples.Count > 0)
        {
            var selected = frame.Samples.MinBy(sample => Math.Abs(X(sample.At) - hovered))!; double x = X(selected.At);
            var cross = Resource("ControlStrongBrush", LabelBrush); var text = Resource("TextPrimaryBrush", Brushes.Black);
            context.DrawLine(new Pen(cross, 1) { DashStyle = new DashStyle([3d, 3d], 0) }, new Point(x + .5, top), new Point(x + .5, bottom));
            var labels = new[] { Format("↓ " + ByteReading.Rate(selected.Down), text, 12), Format("↑ " + ByteReading.Rate(selected.Up), text, 12) };
            double boxWidth = labels.Max(label => label.Width) + 28, boxHeight = 46, left = Math.Max(0, Math.Min(width - boxWidth - 6, x + 10)), y = top + 4;
            context.DrawRoundedRectangle(Resource("DialogTopBrush", Brushes.White), new Pen(cross, 1), new Rect(left, y, boxWidth, boxHeight), 8, 8);
            for (int index = 0; index < labels.Length; index++)
            {
                context.DrawRectangle(index == 0 ? DownBrush : UpBrush, null, new Rect(left + 9, y + 13 + index * 16, 9, 2));
                context.DrawText(labels[index], new Point(left + 23, y + 14 + index * 16 - labels[index].Height / 2));
            }
            context.DrawEllipse(DownBrush, null, new Point(x, Y(selected.Down)), 3.5, 3.5);
            context.DrawEllipse(UpBrush, null, new Point(x, Y(selected.Up)), 3.5, 3.5);
        }
        Schedule(Animate && frame.Active ? 1000d / 60 : frame.NextChangeAt is double deadline ? deadline - now + 1 : null);

        void DrawSeries(bool upload, Brush brush, double stroke, double fill, int series)
        {
            double Value(RatePoint point) => upload ? point.Up : point.Down;
            Point Position(RatePoint point) => new(X(point.At), Y(Value(point)));
            var line = new StreamGeometry(); var area = new StreamGeometry();
            using (var outline = line.Open()) using (var shaded = area.Open())
            {
                foreach (var path in frame.Paths)
                {
                    var first = path.Curves[0].From; var last = path.Curves[^1].To;
                    outline.BeginFigure(Position(first), false, false); shaded.BeginFigure(Position(first), true, true);
                    foreach (var curve in path.Curves)
                    {
                        outline.QuadraticBezierTo(Position(curve.Control), Position(curve.To), true, true);
                        shaded.QuadraticBezierTo(Position(curve.Control), Position(curve.To), true, true);
                    }
                    double end = frame.Tail?.Segment == path.Segment ? width + 12 : X(last.At);
                    if (frame.Tail?.Segment == path.Segment) shaded.LineTo(new Point(end, Y(Value(frame.Tail.To))), true, false);
                    shaded.LineTo(new Point(end, bottom), true, false); shaded.LineTo(new Point(X(first.At), bottom), true, false);
                }
            }
            line.Freeze(); area.Freeze();
            context.DrawGeometry(Gradient(brush, fill, top, bottom), null, area);
            var pen = new Pen(brush, stroke) { LineJoin = PenLineJoin.Round };
            var drawing = new DrawingGroup();
            using (var drawingContext = drawing.Open())
            {
                drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
                drawingContext.DrawGeometry(null, pen, line);
                if (frame.Tail is { } tail)
                {
                    drawingContext.PushOpacity(tail.Opacity); drawingContext.DrawLine(pen, Position(tail.From), Position(tail.To)); drawingContext.Pop();
                }
            }
            if (Resource("DialogTopBrush", Brushes.White) is SolidColorBrush surface && surface.Color.R < 115)
            {
                var image = glow[series]; image.Source = new DrawingImage(drawing); image.Width = width; image.Height = height;
                image.Effect ??= new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
                image.Measure(new Size(width, height)); image.Arrange(new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(image), null, new Rect(0, 0, width, height));
            }
            context.DrawDrawing(drawing);
            if (frame.Tail is { } head)
            {
                var point = Position(head.To); context.PushOpacity(head.Opacity); context.DrawEllipse(brush, null, point, 3, 3);
                double progress = samples.Count == 0 ? 1 : Math.Clamp((now - samples[^1].At) / 1000, 0, 1);
                if (Animate && progress < 1) { context.PushOpacity(.25 * (1 - progress)); context.DrawEllipse(brush, null, point, 3 + 9 * progress, 3 + 9 * progress); context.Pop(); }
                context.Pop();
            }
        }
    }
    private void DrawSpark(DrawingContext context, double width, double height, double now)
    {
        var points = ShownSpark(now).Select(point => new Point(point.X * width / 100, point.Y * height / 28)).ToArray();
        var line = new StreamGeometry(); var area = new StreamGeometry();
        using (var outline = line.Open()) using (var fill = area.Open())
        {
            outline.BeginFigure(points[0], false, false); fill.BeginFigure(points[0], true, true);
            for (int index = 1; index < points.Length; index++)
            {
                var previous = points[index - 1]; var point = points[index]; var middle = new Point((previous.X + point.X) / 2, (previous.Y + point.Y) / 2);
                outline.QuadraticBezierTo(previous, middle, true, true); fill.QuadraticBezierTo(previous, middle, true, true);
            }
            outline.LineTo(points[^1], true, false); fill.LineTo(points[^1], true, false);
            fill.LineTo(new Point(width, height), true, false); fill.LineTo(new Point(0, height), true, false);
        }
        context.DrawGeometry(Gradient(DownBrush, .3, 0, height), null, area);
        context.DrawGeometry(null, new Pen(DownBrush, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
    }
    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private FormattedText Format(string value, Brush brush, double size) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private static Brush Gradient(Brush brush, double alpha, double top, double bottom)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        return new LinearGradientBrush(Color.FromArgb((byte)Math.Round(color.A * alpha), color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B),
            new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute };
    }
}
