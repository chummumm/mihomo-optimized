namespace Mihomo.Desktop.Controls;

internal sealed record RateSample(double At, double Up, double Down, int Segment, double Span);
internal sealed record RatePoint(double At, double Up, double Down, double[]? Blend = null);
internal sealed record RateCurve(RatePoint From, RatePoint Control, RatePoint To);
internal sealed record RatePath(int Segment, IReadOnlyList<RateCurve> Curves);
internal sealed record RateTail(int Segment, RatePoint From, RatePoint To, double Opacity);
internal sealed record RateFrame(double EndAt, double Maximum, IReadOnlyList<RatePath> Paths,
    RateTail? Tail, IReadOnlyList<RateSample> Samples, bool Active, double? NextChangeAt);

/// <summary>
/// Native port of Connections/chart-motion.ts. Only the newest local controls
/// ease for 200 ms; measured samples and older quadratics remain unchanged.
/// A monotonic local clock needs none of the web controller's clock correction.
/// </summary>
internal sealed class ThroughputMotion
{
    private readonly record struct Heights(double Up, double Down);
    private sealed class Transition(RateSample sample, RateSample? previous, Heights[] from, Heights[] target, double startedAt, double duration)
    {
        public RateSample Sample { get; } = sample;
        public RateSample? Previous { get; } = previous;
        public Heights[] From { get; } = from;
        public Heights[] Target { get; set; } = target;
        public double StartedAt { get; } = startedAt;
        public double Duration { get; } = duration;
    }
    private double? frameAt;
    private int? segment;
    private double maximum = 1;
    private Transition? headTransition;
    private Transition? previousTransition;

    public void Reset()
    {
        frameAt = null; segment = null; maximum = 1; headTransition = previousTransition = null;
    }
    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
    private static double Progress(Transition transition, double now) => 1 - Math.Pow(1 - Clamp((now - transition.StartedAt) / transition.Duration), 3);
    private static Heights[] Shown(Transition transition, double now)
    {
        double progress = Progress(transition, now);
        return transition.From.Select((from, index) => new Heights(from.Up + (transition.Target[index].Up - from.Up) * progress,
            from.Down + (transition.Target[index].Down - from.Down) * progress)).ToArray();
    }
    private static RatePoint Point(RateSample sample) => new(sample.At, sample.Up, sample.Down);
    private static RatePoint Between(RatePoint a, RatePoint b, double t) => new(
        a.At + (b.At - a.At) * t, a.Up + (b.Up - a.Up) * t, a.Down + (b.Down - a.Down) * t,
        a.Blend is null && b.Blend is null ? null : Enumerable.Range(0, 5).Select(index =>
            (a.Blend?[index] ?? 0) + ((b.Blend?[index] ?? 0) - (a.Blend?[index] ?? 0)) * t).ToArray());
    private static (RateCurve Left, RateCurve Right) Split(RateCurve curve, double t)
    {
        var a = Between(curve.From, curve.Control, t); var b = Between(curve.Control, curve.To, t); var point = Between(a, b, t);
        return (new RateCurve(curve.From, a, point), new RateCurve(point, b, curve.To));
    }
    private static double ParameterAt(RateCurve curve, double at)
    {
        if (at <= curve.From.At) return 0;
        if (at >= curve.To.At) return 1;
        double low = 0, high = 1;
        for (int index = 0; index < 32; index++)
        {
            double middle = (low + high) / 2;
            double a = curve.From.At + (curve.Control.At - curve.From.At) * middle;
            double b = curve.Control.At + (curve.To.At - curve.Control.At) * middle;
            if (a + (b - a) * middle < at) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }
    private static RateCurve? Clip(RateCurve curve, double start, double end)
    {
        if (curve.To.At < start || curve.From.At > end) return null;
        double from = ParameterAt(curve, start), to = ParameterAt(curve, end);
        if (to <= from) return null;
        var prefix = to == 1 ? curve : Split(curve, to).Left;
        return from == 0 ? prefix : Split(prefix, from / to).Right;
    }
    private static double Peak(RateCurve curve, bool upload)
    {
        double a = upload ? curve.From.Up : curve.From.Down;
        double b = upload ? curve.Control.Up : curve.Control.Down;
        double c = upload ? curve.To.Up : curve.To.Down;
        double t = (a - b) / (a - 2 * b + c);
        double peak = t > 0 && t < 1 ? (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c : 0;
        return Math.Max(Math.Max(a, c), peak);
    }

    public RateFrame Frame(IReadOnlyList<RateSample> history, double now, bool reduced)
    {
        var latest = history.LastOrDefault();
        double elapsed = frameAt is null ? 0 : now - frameAt.Value;
        bool discontinuity = frameAt is null || elapsed < 0 || elapsed > 10000 || latest?.Segment != segment;
        double startAt = now - 60000, age = latest is null ? double.PositiveInfinity : now - latest.At;
        bool live = latest is not null && age >= 0 && age < 2000;
        var previous = history.Count > 1 ? history[^2] : null;
        bool resetHead = !live || discontinuity || reduced;
        var oldTransition = headTransition;
        var retired = resetHead ? null : previousTransition;
        if (!resetHead && oldTransition is not null && !ReferenceEquals(oldTransition.Sample, latest) && ReferenceEquals(previous, oldTransition.Sample))
        {
            double remaining = oldTransition.StartedAt + oldTransition.Duration - now;
            retired = remaining > 0 ? new Transition(oldTransition.Sample, oldTransition.Previous, Shown(oldTransition, now), oldTransition.Target, now, remaining) : null;
        }
        if (retired is not null && (!ReferenceEquals(retired.Sample, previous) || Progress(retired, now) == 1)) retired = null;
        previousTransition = retired;

        var controls = new RatePoint?[5];
        RatePoint Control(RatePoint point, int index)
        {
            controls[index] = point;
            var blend = new double[5]; blend[index] = 1;
            return point with { Blend = blend };
        }
        var newest = live ? Control(Point(latest!), 2) : latest is null ? null : Point(latest);
        RatePoint Reading(int index)
        {
            if (index == history.Count - 1) return newest!;
            return retired is not null && ReferenceEquals(history[index], retired.Sample) ? Control(Point(history[index]), 4) : Point(history[index]);
        }
        double peak = 1024;
        bool active = live;
        double? nextChangeAt = null;
        void Schedule(double at) { if (at > now && (nextChangeAt is null || at < nextChangeAt.Value)) nextChangeAt = at; }
        if (latest is not null)
        {
            if (live) { Schedule(latest.At + 1500); Schedule(latest.At + 2000); }
            Schedule(latest.At + 61000);
        }
        var paths = new List<RatePath>();
        var eligible = new List<RateSample>();
        int start = 0;
        while (start < history.Count)
        {
            int end = start + 1;
            while (end < history.Count && history[end].Segment == history[start].Segment) end++;
            var curves = new List<RateCurve>();
            var prior = Reading(start);
            void Append(RateCurve curve)
            {
                if (curve.To.At >= startAt) active = true;
                Schedule(curve.To.At + 60000);
                var clipped = Clip(curve, startAt, now);
                if (clipped is null) return;
                curves.Add(clipped); peak = Math.Max(peak, Math.Max(Peak(clipped, true), Peak(clipped, false)));
            }
            for (int index = start + 1; index < end; index++)
            {
                var to = Between(Reading(index - 1), Reading(index), .5);
                if (live && index == history.Count - 1) to = Control(to, 0);
                else if (retired is not null && ReferenceEquals(history[index], retired.Sample)) to = Control(to, 3);
                Append(new RateCurve(prior, Reading(index - 1), to)); prior = to;
            }
            var last = Reading(end - 1);
            if (prior.At < last.At)
            {
                var middle = Between(prior, last, .5);
                Append(new RateCurve(prior, live && end == history.Count ? Control(middle, 1) : middle, last));
            }
            if (curves.Count > 0) paths.Add(new RatePath(history[start].Segment, curves));
            for (int index = start; index < end; index++)
            {
                var sample = history[index]; Schedule(sample.At + 60000);
                if (sample.At < startAt || sample.At > now) continue;
                eligible.Add(sample); peak = Math.Max(peak, Math.Max(sample.Up, sample.Down));
            }
            start = end;
        }
        maximum += (peak * 1.2 - maximum) * (reduced ? 1 : .06);
        double progress = 1;
        Heights Normalized(RatePoint point) => new(Clamp(point.Up / maximum), Clamp(point.Down / maximum));
        if (!live) headTransition = null;
        else
        {
            controls[0] ??= Point(latest!); controls[1] ??= Point(latest!);
            var target = controls.Take(3).Select(point => Normalized(point!)).ToArray();
            if (oldTransition is null || resetHead)
                headTransition = new Transition(latest!, previous, target, target, now - 200, 200);
            else if (!ReferenceEquals(oldTransition.Sample, latest))
            {
                var shown = Shown(oldTransition, now); var head = shown[2]; var from = shown;
                if (!ReferenceEquals(oldTransition.Previous, previous))
                {
                    from = retired is not null ? [head, head, head] : controls.Take(3).Select((point, index) =>
                    {
                        double weight = new[] { .5, .75, 1 }[index];
                        return new Heights(Clamp((point!.Up - weight * latest!.Up) / maximum + weight * head.Up),
                            Clamp((point.Down - weight * latest.Down) / maximum + weight * head.Down));
                    }).ToArray();
                }
                headTransition = new Transition(latest!, previous, from, target, now, 200); progress = 0;
            }
            else { oldTransition.Target = target; progress = Progress(oldTransition, now); }
            if (retired is not null)
            {
                controls[3] ??= Point(retired.Sample); controls[4] ??= Point(retired.Sample);
                retired.Target = [Normalized(controls[3]!), Normalized(Between(controls[3]!, controls[4]!, .5)), Normalized(controls[4]!)];
            }
        }
        var transition = headTransition;
        double retiredProgress = retired is null ? 1 : Progress(retired, now);
        RatePoint DrawPoint(RatePoint point)
        {
            if (point.Blend is null || transition is null || progress == 1 && retiredProgress == 1) return point;
            double Value(bool upload)
            {
                double currentOffset = 0, retiredOffset = 0;
                for (int index = 0; index < 5; index++)
                {
                    double weight = point.Blend[index]; if (weight == 0) continue;
                    double rawControl = (upload ? controls[index]!.Up : controls[index]!.Down) / maximum;
                    if (index < 3) currentOffset += weight * ((upload ? transition.From[index].Up : transition.From[index].Down) - rawControl);
                    else if (retired is not null)
                    {
                        var from = retired.From[index == 3 ? 0 : 2]; retiredOffset += weight * ((upload ? from.Up : from.Down) - rawControl);
                    }
                }
                double raw = (upload ? point.Up : point.Down) / maximum;
                double fromHeight = Clamp(raw + currentOffset + retiredOffset);
                double current = fromHeight + (Clamp(raw + retiredOffset) - fromHeight) * progress;
                double to = Clamp(raw + currentOffset) + (Clamp(raw) - Clamp(raw + currentOffset)) * progress;
                return (current + (to - current) * retiredProgress) * maximum;
            }
            return new RatePoint(point.At, Value(true), Value(false));
        }
        var drawn = paths.Select(path => new RatePath(path.Segment, path.Curves.Select(curve => new RateCurve(DrawPoint(curve.From), DrawPoint(curve.Control), DrawPoint(curve.To))).ToArray())).ToArray();
        var shownHead = live ? DrawPoint(newest!) : null;
        var tail = shownHead is null ? null : new RateTail(latest!.Segment, shownHead, shownHead with { At = now }, Math.Min(1, (2000 - age) / 500));
        frameAt = now; segment = latest?.Segment;
        return new RateFrame(now, maximum, drawn, tail, eligible, active, nextChangeAt);
    }
}
