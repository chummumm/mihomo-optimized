import {
    rateHistoryState, RATE_CLOCK_MARGIN_MS, RATE_STALE_MS, RATE_WINDOW_MS,
    type RateReading, type RateSample,
} from './throughput-history'

interface RateCurve {
    from: RateReading
    control: RateReading
    to: RateReading
}

interface ChartPath {
    segment: number
    curves: RateCurve[]
}

interface DisplayTail {
    segment: number
    from: RateReading
    to: RateReading
    opacity: number
}

interface ChartFrame {
    endAt: number
    ymax: number
    paths: ChartPath[]
    head: RateReading | undefined
    tail: DisplayTail | undefined
    samples: readonly RateSample[]
    projectAt: (at: number) => number
    active: boolean
    nextChangeAt: number | undefined
}

function between (a: RateReading, b: RateReading, t: number): RateReading {
    return { at: a.at + (b.at - a.at) * t, up: a.up + (b.up - a.up) * t, down: a.down + (b.down - a.down) * t }
}

function split (curve: RateCurve, t: number): [RateCurve, RateCurve] {
    const a = between(curve.from, curve.control, t)
    const b = between(curve.control, curve.to, t)
    const point = between(a, b, t)
    return [{ from: curve.from, control: a, to: point }, { from: point, control: b, to: curve.to }]
}

function parameterAt (curve: RateCurve, at: number) {
    if (at <= curve.from.at) return 0
    if (at >= curve.to.at) return 1
    // Time is monotonic along these quadratics, including a zero starting
    // tangent. Bisection avoids a fragile quadratic root near a straight line.
    let low = 0
    let high = 1
    for (let i = 0; i < 32; i++) {
        const middle = (low + high) / 2
        const a = curve.from.at + (curve.control.at - curve.from.at) * middle
        const b = curve.control.at + (curve.to.at - curve.control.at) * middle
        if (a + (b - a) * middle < at) low = middle
        else high = middle
    }
    return (low + high) / 2
}

function clipCurve (curve: RateCurve, start: number, end: number): RateCurve | undefined {
    if (curve.to.at < start || curve.from.at > end) return undefined
    const from = parameterAt(curve, start)
    const to = parameterAt(curve, end)
    if (to <= from) return undefined
    const prefix = to === 1 ? curve : split(curve, to)[0]
    return from === 0 ? prefix : split(prefix, from / to)[1]
}

function curvePeak (curve: RateCurve, key: 'up' | 'down') {
    const a = curve.from[key]
    const b = curve.control[key]
    const c = curve.to[key]
    const t = (a - b) / (a - 2 * b + c)
    const peak = t > 0 && t < 1 ? (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c : 0
    return Math.max(a, c, peak)
}

// Only the current end of the old quadratic may change as observations arrive.
// A short, separate tail displays the latest known value at the right edge; it
// is not an observation and never enters measured history or tooltip records.
export class ThroughputMotion {
    private history: readonly RateSample[] | undefined
    private frameAt: number | undefined
    private shift = 0
    private phase = 0
    private segment: number | undefined
    private ymax = 1

    reset () {
        this.history = undefined
        this.frameAt = undefined
        this.phase = 0
        this.segment = undefined
    }

    frame (history: readonly RateSample[], now: number, reducedMotion: boolean): ChartFrame {
        const state = rateHistoryState(history)
        const latest = history[history.length - 1]
        const elapsed = this.frameAt === undefined ? 0 : now - this.frameAt
        const shift = this.history === history ? state.shift - this.shift : 0
        const discontinuity = this.frameAt === undefined || this.history !== history ||
            elapsed < 0 || elapsed > RATE_STALE_MS || latest?.segment !== this.segment
        const phase = this.phase + shift
        if (reducedMotion || discontinuity || Math.abs(phase) > RATE_CLOCK_MARGIN_MS) this.phase = 0
        else {
            // Compensate the sample calibration before gradually absorbing it.
            // Large corrections rebuild instead of queuing minutes of catch-up.
            const correction = Math.min(Math.abs(phase), elapsed * 0.25)
            this.phase = phase - Math.sign(phase) * correction
        }
        const endAt = now + this.phase
        const startAt = endAt - RATE_WINDOW_MS
        const age = latest === undefined ? Infinity : now - latest.at
        const live = latest !== undefined && state.pending === latest && age >= 0 && age < 2000
        // A small negative phase must not hide newly received data offscreen.
        // Compress only the last second of drawing time. Split crossing curves
        // before projection, so the older prefix and all real timestamps stay put.
        const anchor = endAt - 1000
        const project = live && latest.at > endAt
        const scale = project ? (endAt - anchor) / (latest.at - anchor) : 1
        const projectAt = (at: number) => !project || at <= anchor ? at : Math.min(endAt, anchor + (at - anchor) * scale)
        const projectPoint = (point: RateReading): RateReading => ({ at: projectAt(point.at), up: point.up, down: point.down })
        const rawEndAt = project ? latest.at : endAt
        const paths: ChartPath[] = []
        const eligible: RateSample[] = []
        const tail: DisplayTail | undefined = live ? {
            segment: latest.segment,
            from: projectPoint(latest),
            to: { at: endAt, up: latest.up, down: latest.down },
            opacity: Math.min(1, (2000 - age) / 500),
        } : undefined
        let peak = 1024
        let active = tail !== undefined
        let nextChangeAt: number | undefined
        const schedule = (at: number) => {
            if (at > now && (nextChangeAt === undefined || at < nextChangeAt)) nextChangeAt = at
        }

        if (latest !== undefined) {
            if (live) {
                schedule(latest.at + 1500)
                schedule(latest.at + 2000)
            }
            schedule(latest.at + RATE_WINDOW_MS + RATE_CLOCK_MARGIN_MS)
        }
        let start = 0
        while (start < history.length) {
            let end = start + 1
            while (end < history.length && history[end].segment === history[start].segment) end++
            const curves: RateCurve[] = []
            let previous: RateReading = history[start]
            const append = (curve: RateCurve) => {
                if (projectAt(curve.to.at) >= startAt) active = true
                schedule(curve.to.at + RATE_WINDOW_MS)
                const clipped = clipCurve(curve, startAt, rawEndAt)
                if (clipped === undefined) return
                const pieces = project && clipped.from.at < anchor && clipped.to.at > anchor
                    ? split(clipped, parameterAt(clipped, anchor)) : [clipped]
                for (const piece of pieces) {
                    const drawn = { from: projectPoint(piece.from), control: projectPoint(piece.control), to: projectPoint(piece.to) }
                    curves.push(drawn)
                    peak = Math.max(peak, curvePeak(drawn, 'up'), curvePeak(drawn, 'down'))
                }
            }
            // Pending buckets are shown immediately. Replacing one only changes
            // the nearby tail, not the already independent historical curves.
            for (let i = start + 1; i < end; i++) {
                const to = between(history[i - 1], history[i], 0.5)
                append({ from: previous, control: history[i - 1], to })
                previous = to
            }
            const last = history[end - 1]
            if (previous.at < last.at) {
                // A straight quadratic is the original terminal line, kept
                // separate from the held-value display tail.
                append({ from: previous, control: between(previous, last, 0.5), to: last })
            }
            if (curves.length > 0) paths.push({ segment: history[start].segment, curves })
            for (let i = start; i < end; i++) {
                const sample = history[i]
                schedule(sample.at + RATE_WINDOW_MS)
                const at = projectAt(sample.at)
                if (at < startAt || at > endAt) continue
                eligible.push(sample)
                peak = Math.max(peak, sample.up, sample.down)
            }
            start = end
        }

        // Restore the 0.4.10 scale response in both directions. A new peak does
        // not instantly compress the whole waveform; measurements stay intact.
        const targetMax = peak * 1.2
        this.ymax += (targetMax - this.ymax) * (reducedMotion ? 1 : 0.06)
        this.history = history
        this.frameAt = now
        this.shift = state.shift
        this.segment = latest?.segment
        return { endAt, ymax: this.ymax, paths, head: tail?.to, tail, samples: eligible, projectAt, active, nextChangeAt }
    }
}
