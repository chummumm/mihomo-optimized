import type { ConnectionSnapshot } from '../../lib/connection-stream'

import { RATE_STALE_MS } from './throughput-history'

// Legacy controllers send one snapshot per second. Closely spaced callbacks
// cannot tell us when those snapshots were sampled; start a new baseline rather
// than interpreting queued bytes as a new high-speed measurement.
const LEGACY_MIN_INTERVAL_MS = 250

interface SampleTime {
    receivedAt: number
    sourceAt?: number
    sequence?: number
    offset: number
}

export interface SampleReading {
    at: number
    shift: number
    restart: boolean
}

// Maps the core's monotonic sampling clock onto the browser's drawing clock.
// Its offset can only move towards a lower observed delivery delay. Moving all
// existing points by the same offset preserves their real sampling intervals.
export class SampleClock {
    private generation: number | undefined
    private previous: SampleTime | undefined

    reset () {
        this.generation = undefined
        this.previous = undefined
    }

    read (snapshot: ConnectionSnapshot): SampleReading | undefined {
        const { generation, receivedAt } = snapshot.stream
        if (!Number.isFinite(receivedAt)) return undefined
        const timed = snapshot.sampledAt !== undefined || snapshot.sequence !== undefined
        if (timed && (!Number.isFinite(snapshot.sampledAt) || snapshot.sampledAt! < 0 ||
            !Number.isSafeInteger(snapshot.sequence) || snapshot.sequence! < 1)) return undefined

        const sourceAt = timed ? snapshot.sampledAt : undefined
        const sequence = timed ? snapshot.sequence : undefined
        let previous = this.generation === generation ? this.previous : undefined
        if ((previous?.sourceAt !== undefined) !== timed) previous = undefined
        if (previous?.sequence !== undefined && sequence! <= previous.sequence) return undefined

        const offset = sourceAt === undefined ? 0 : Math.min(previous?.offset ?? Infinity, receivedAt - sourceAt)
        const at = sourceAt === undefined ? receivedAt : sourceAt + offset
        const elapsed = previous === undefined ? 0 : sourceAt === undefined
            ? receivedAt - previous.receivedAt
            : sourceAt - previous.sourceAt!
        this.generation = generation
        this.previous = { receivedAt, sourceAt, sequence, offset }
        return {
            at,
            shift: previous === undefined ? 0 : offset - previous.offset,
            restart: previous === undefined || elapsed <= 0 || elapsed > RATE_STALE_MS ||
                (!timed && elapsed < LEGACY_MIN_INTERVAL_MS),
        }
    }
}
