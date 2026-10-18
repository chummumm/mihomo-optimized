import {
    rateHistoryState, RATE_CLOCK_MARGIN_MS, RATE_STALE_MS, RATE_WINDOW_MS,
    type RateReading, type RateSample,
} from './throughput-history'

interface CurvePoint extends RateReading {
    blend?: number[]
}

interface RateCurve {
    from: CurvePoint
    control: CurvePoint
    to: CurvePoint
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
    // Geometry uses the current axis's rate units. Only samples are measured
    // values; the live endpoint can have a short presentation transition.
    paths: ChartPath[]
    head: RateReading | undefined
    tail: DisplayTail | undefined
    samples: readonly RateSample[]
    projectAt: (at: number) => number
    active: boolean
    nextChangeAt: number | undefined
}

const HEAD_TRANSITION_MS = 200

interface HeadTransition {
    sample: RateSample
    previous: RateSample | undefined
    from: Heights[]
    target: Heights[]
    startedAt: number
    duration: number
}

type Heights = Pick<RateReading, 'up' | 'down'>

function clampHeight (height: number) {
    return Math.max(0, Math.min(1, height))
}

function transitionProgress (transition: HeadTransition, now: number) {
    const elapsed = clampHeight((now - transition.startedAt) / transition.duration)
    return 1 - (1 - elapsed) ** 3
}

function transitionHeights (transition: HeadTransition, now: number) {
    const progress = transitionProgress(transition, now)
    return transition.from.map((from, i) => ({
        up: from.up + (transition.target[i].up - from.up) * progress,
        down: from.down + (transition.target[i].down - from.down) * progress,
    }))
}

function between (a: CurvePoint, b: CurvePoint, t: number): CurvePoint {
    return {
        at: a.at + (b.at - a.at) * t,
        up: a.up + (b.up - a.up) * t,
        down: a.down + (b.down - a.down) * t,
        blend: a.blend === undefined && b.blend === undefined ? undefined
            : Array.from({ length: 5 }, (_, i) => (a.blend?.[i] ?? 0) + ((b.blend?.[i] ?? 0) - (a.blend?.[i] ?? 0)) * t),
    }
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
    private headTransition: HeadTransition | undefined
    private previousTransition: HeadTransition | undefined

    reset () {
        this.history = undefined
        this.frameAt = undefined
        this.phase = 0
        this.segment = undefined
        this.headTransition = undefined
        this.previousTransition = undefined
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
        const projectPoint = (point: CurvePoint): CurvePoint => ({ at: projectAt(point.at), up: point.up, down: point.down, blend: point.blend })
        const rawEndAt = project ? latest.at : endAt
        const paths: ChartPath[] = []
        const eligible: RateSample[] = []
        const previous = history[history.length - 2]
        const resetHead = !live || discontinuity || reducedMotion || Math.abs(phase) > RATE_CLOCK_MARGIN_MS
        const oldTransition = this.headTransition
        let retired = resetHead ? undefined : this.previousTransition
        if (!resetHead && oldTransition !== undefined && oldTransition.sample !== latest && previous === oldTransition.sample) {
            const remaining = oldTransition.startedAt + oldTransition.duration - now
            // The prior midpoint and head now belong to the new curve. Finish
            // their existing deadline instead of snapping them to raw history.
            retired = remaining > 0 ? {
                ...oldTransition, from: transitionHeights(oldTransition, now), startedAt: now, duration: remaining,
            } : undefined
        }
        if (retired !== undefined && (retired.sample !== previous || transitionProgress(retired, now) === 1)) retired = undefined
        this.previousTransition = retired

        // Only five local controls can be moving: the newest midpoint, terminal
        // control and head, plus the preceding midpoint/head during a handoff.
        // Their influences follow exact clipping and time projection.
        const controls: CurvePoint[] = []
        const control = (point: CurvePoint, index: number): CurvePoint => {
            controls[index] = point
            return { ...point, blend: Array.from({ length: 5 }, (_, i) => i === index ? 1 : 0) }
        }
        const newest = live ? control(latest, 2) : latest
        const reading = (index: number): CurvePoint => {
            if (index === history.length - 1) return newest
            return retired !== undefined && history[index] === retired.sample ? control(history[index], 4) : history[index]
        }
        let peak = 1024
        let active = live
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
            let previous: CurvePoint = reading(start)
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
                let to = between(reading(i - 1), reading(i), 0.5)
                if (live && i === history.length - 1) to = control(to, 0)
                else if (retired !== undefined && history[i] === retired.sample) to = control(to, 3)
                append({ from: previous, control: reading(i - 1), to })
                previous = to
            }
            const last = reading(end - 1)
            if (previous.at < last.at) {
                // A straight quadratic is the original terminal line, kept
                // separate from the held-value display tail.
                const middle = between(previous, last, 0.5)
                append({ from: previous, control: live && end === history.length ? control(middle, 1) : middle, to: last })
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

        // Cache screen heights, not old rates: a new peak changes ymax in this
        // same frame. Reprojecting the old rate would still jump the endpoint.
        let progress = 1
        if (!live) this.headTransition = undefined
        else {
            // A single first reading has a head without an invented curve.
            controls[0] ??= latest
            controls[1] ??= latest
            const normalized = (point: CurvePoint): Heights => ({ up: clampHeight(point.up / this.ymax), down: clampHeight(point.down / this.ymax) })
            const target = controls.slice(0, 3).map(normalized)
            if (oldTransition === undefined || resetHead) {
                this.headTransition = { sample: latest, previous, from: target, target, startedAt: now - HEAD_TRANSITION_MS, duration: HEAD_TRANSITION_MS }
            } else if (oldTransition.sample !== latest) {
                // Advance the previous target before retargeting. Frequent
                // updates never queue animations or restart from an old value.
                const shown = transitionHeights(oldTransition, now)
                const head = shown[2]
                let from = shown
                if (oldTransition.previous !== previous) {
                    from = retired !== undefined ? [head, head, head] : controls.slice(0, 3).map((point, i) => {
                        const weight = [0.5, 0.75, 1][i]
                        return {
                            up: clampHeight((point.up - weight * latest.up) / this.ymax + weight * head.up),
                            down: clampHeight((point.down - weight * latest.down) / this.ymax + weight * head.down),
                        }
                    })
                }
                this.headTransition = { sample: latest, previous, from, target, startedAt: now, duration: HEAD_TRANSITION_MS }
                progress = 0
            } else {
                // Scale motion alone updates the destination without restarting
                // the finite transition on every animation frame.
                oldTransition.target = target
                progress = transitionProgress(oldTransition, now)
            }
            if (retired !== undefined) {
                controls[3] ??= retired.sample
                controls[4] ??= retired.sample
                retired.target = [normalized(controls[3]), normalized(between(controls[3], controls[4], 0.5)), normalized(controls[4])]
            }
        }
        const transition = this.headTransition
        const retiredProgress = retired === undefined ? 1 : transitionProgress(retired, now)
        const drawPoint = (point: CurvePoint): RateReading => {
            if (point.blend === undefined || transition === undefined || (progress === 1 && retiredProgress === 1)) return point
            const value = (key: 'up' | 'down') => {
                let currentOffset = 0
                let retiredOffset = 0
                for (let i = 0; i < 5; i++) {
                    const weight = point.blend![i]
                    if (weight === 0) continue
                    const raw = controls[i][key] / this.ymax
                    if (i < 3) currentOffset += weight * (transition.from[i][key] - raw)
                    else if (retired !== undefined) retiredOffset += weight * (retired.from[i === 3 ? 0 : 2][key] - raw)
                }
                const raw = point[key] / this.ymax
                // Blend the local shapes after clamping. Large raw peaks must
                // not create a hump above the still-moving head. Each finished
                // transition returns exactly to the existing clamped curve.
                const from = clampHeight(raw + currentOffset + retiredOffset)
                const current = from + (clampHeight(raw + retiredOffset) - from) * progress
                const to = clampHeight(raw + currentOffset) + (clampHeight(raw) - clampHeight(raw + currentOffset)) * progress
                return (current + (to - current) * retiredProgress) * this.ymax
            }
            return { at: point.at, up: value('up'), down: value('down') }
        }
        for (const path of paths) {
            for (const curve of path.curves) {
                curve.from = drawPoint(curve.from)
                curve.control = drawPoint(curve.control)
                curve.to = drawPoint(curve.to)
            }
        }
        const shown = live ? drawPoint(newest) : undefined
        const tail: DisplayTail | undefined = shown === undefined ? undefined : {
            segment: latest.segment,
            from: { at: projectAt(shown.at), up: shown.up, down: shown.down },
            to: { at: endAt, up: shown.up, down: shown.down },
            opacity: Math.min(1, (2000 - age) / 500),
        }
        this.history = history
        this.frameAt = now
        this.shift = state.shift
        this.segment = latest?.segment
        return { endAt, ymax: this.ymax, paths, head: tail?.to, tail, samples: eligible, projectAt, active, nextChangeAt }
    }
}
