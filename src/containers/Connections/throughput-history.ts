export const RATE_WINDOW_MS = 60_000
export const RATE_STALE_MS = 10_000
// A completed bucket and its neighbour fix the old quadratic. The buffer also
// allows half a second of delivery jitter, without extrapolating future traffic.
export const RATE_DRAW_DELAY_MS = 3000
export const RATE_CLOCK_MARGIN_MS = 1000
export const RATE_SAMPLE_LIMIT = 68

export interface RateReading {
    up: number
    down: number
    at: number
}

export interface RateSample extends RateReading {
    span: number
    segment: number
}

interface Totals {
    uploadTotal: number
    downloadTotal: number
}

interface HistoryState {
    shift: number
    pending?: RateSample
}

const historyStates = new WeakMap<readonly RateSample[], HistoryState>()

export function rateHistoryState (samples: readonly RateSample[]): Readonly<HistoryState> {
    return historyStates.get(samples) ?? { shift: 0 }
}

export function pruneRateSamples (samples: RateSample[], now: number) {
    let expired = 0
    const retainedWindow = RATE_WINDOW_MS + RATE_DRAW_DELAY_MS + RATE_CLOCK_MARGIN_MS
    while (expired < samples.length && now - samples[expired].at > retainedWindow) expired++
    // Two preceding controls preserve the left-edge shape. When the entire
    // window expires, its guards must expire as well.
    const remove = expired === samples.length ? expired : Math.max(0, expired - 2)
    if (remove > 0) samples.splice(0, remove)
}

export function rateSampleX (at: number, now: number, width: number) {
    return width * (1 - (now - at) / RATE_WINDOW_MS)
}

// One time bucket per second, weighted by its measured duration. Even if a
// controller sends snapshots faster than once a second, one minute stays bounded.
export class ThroughputHistory {
    readonly samples: RateSample[] = []
    private readonly state: HistoryState = { shift: 0 }
    private previous: (Totals & { at: number }) | undefined
    private segment = 0

    constructor () {
        historyStates.set(this.samples, this.state)
    }

    resetBaseline () {
        this.previous = undefined
        this.state.pending = undefined
    }

    shiftTime (milliseconds: number) {
        if (milliseconds === 0) return
        this.state.shift += milliseconds
        for (const sample of this.samples) sample.at += milliseconds
        if (this.previous !== undefined) this.previous.at += milliseconds
    }

    feed (totals: Totals, now: number): RateReading {
        pruneRateSamples(this.samples, now)
        const previous = this.previous
        const valid = Number.isFinite(now) && Number.isFinite(totals.uploadTotal) && Number.isFinite(totals.downloadTotal) && totals.uploadTotal >= 0 && totals.downloadTotal >= 0
        this.previous = valid ? { ...totals, at: now } : undefined
        const elapsed = previous === undefined ? 0 : now - previous.at
        if (!valid || previous === undefined || elapsed <= 0 || elapsed > RATE_STALE_MS || totals.uploadTotal < previous.uploadTotal || totals.downloadTotal < previous.downloadTotal) {
            this.segment++
            this.state.pending = undefined
            return { up: 0, down: 0, at: now }
        }

        const up = (totals.uploadTotal - previous.uploadTotal) * 1000 / elapsed
        const down = (totals.downloadTotal - previous.downloadTotal) * 1000 / elapsed
        const sample = { up, down, at: now, span: elapsed, segment: this.segment }
        const last = this.samples[this.samples.length - 1]
        if (last !== undefined && last.segment === sample.segment && Math.floor(last.at / 1000) === Math.floor(now / 1000)) {
            const span = last.span + elapsed
            sample.up = (last.up * last.span + up * elapsed) / span
            sample.down = (last.down * last.span + down * elapsed) / span
            sample.span = span
            this.samples[this.samples.length - 1] = sample
        } else {
            this.samples.push(sample)
            if (this.samples.length > RATE_SAMPLE_LIMIT) this.samples.shift()
        }
        this.state.pending = sample
        return { up, down, at: now }
    }
}

