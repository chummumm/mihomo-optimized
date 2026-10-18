import { RATE_STALE_MS, type RateReading, type RateSample } from './throughput-history'

const TAIL_TWEEN_MS = 250
const SCALE_DECAY_MS = 520

type Rates = Pick<RateReading, 'up' | 'down'>

interface ChartFrame {
    ymax: number
    head: RateReading | undefined
}

// Presentation only: measurements and their timestamps stay untouched. A new
// target redirects the one active tail transition instead of queuing animations.
export class ThroughputMotion {
    private history: readonly RateSample[] | undefined
    private frameAt: number | undefined
    private target: RateSample | undefined
    private from: Rates | undefined
    private startedAt = 0
    private ymax = 0

    reset () {
        this.history = undefined
        this.frameAt = undefined
        this.target = undefined
        this.from = undefined
        this.ymax = 0
    }

    frame (history: readonly RateSample[], now: number, reducedMotion: boolean): ChartFrame {
        const latest = history[history.length - 1]
        const previous = this.target
        const elapsed = this.frameAt === undefined ? 0 : now - this.frameAt
        const fresh = latest !== undefined && now - latest.at <= RATE_STALE_MS
        const discontinuity = this.frameAt === undefined || this.history !== history ||
            elapsed < 0 || elapsed > RATE_STALE_MS || latest === undefined || previous === undefined ||
            latest.segment !== previous.segment || latest.at < previous.at || latest.at - previous.at > RATE_STALE_MS
        const immediate = reducedMotion || discontinuity
        let shown = this.ratesAt(now)

        if (!fresh) {
            this.target = undefined
            this.from = undefined
            shown = undefined
        } else if (immediate || previous === undefined || latest.at !== previous.at || latest.up !== previous.up || latest.down !== previous.down) {
            this.from = immediate || shown === undefined ? { up: latest.up, down: latest.down } : shown
            this.target = { ...latest }
            this.startedAt = now
            shown = this.ratesAt(now)
        }

        // The x coordinate always belongs to the real sample. After updates stop,
        // the head ages leftwards and disappears; it never fills the gap to "now".
        const head = fresh && shown !== undefined ? { ...shown, at: latest.at } : undefined
        let peak = Math.max(1024, head?.up ?? 0, head?.down ?? 0)
        for (const sample of history) peak = Math.max(peak, sample.up, sample.down)
        const targetMax = peak * 1.2
        // Expand immediately so a new measured peak is never clipped. Shrinking
        // depends on elapsed time, so dropped frames do not slow down the easing.
        this.ymax = immediate || targetMax >= this.ymax
            ? targetMax
            : targetMax + (this.ymax - targetMax) * Math.exp(-elapsed / SCALE_DECAY_MS)
        this.history = history
        this.frameAt = now
        return { ymax: this.ymax, head }
    }

    private ratesAt (now: number): Rates | undefined {
        if (this.target === undefined || this.from === undefined) return undefined
        const progress = Math.max(0, Math.min(1, (now - this.startedAt) / TAIL_TWEEN_MS))
        const eased = 1 - Math.pow(1 - progress, 3)
        return {
            up: this.from.up + (this.target.up - this.from.up) * eased,
            down: this.from.down + (this.target.down - this.from.down) * eased,
        }
    }
}
