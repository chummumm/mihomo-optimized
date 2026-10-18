import { useLayoutEffect, useMemo, useSyncExternalStore } from 'react'

import type * as API from '@lib/request'
import type { ConnectionSnapshot, ConnectionStreamReader } from '@lib/connection-stream'

import { SampleClock } from './sample-clock'
import { ALL_CONNECTIONS, getConnectionSourceKey } from './source'
import { RATE_STALE_MS, ThroughputHistory, type RateReading, type RateSample } from './throughput-history'

export type Connection = API.Connections & { sourceKey: string, completed?: boolean, uploadSpeed: number, downloadSpeed: number }

export interface FormatConnection {
    id: string
    host: string
    chains: string
    rule: string
    time: number
    upload: number
    download: number
    type: string
    network: string
    process?: string
    sourceIP: string
    speed: {
        upload: number
        download: number
    }
    completed: boolean
    original: Connection
}

export const CLOSED_CONNECTION_LIMIT = 5000
const PUBLICATION_INTERVAL_MS = 100

export interface ConnectionDevice {
    key: string
    label: string
    number: number
}

interface ConnectionState {
    connections: Connection[]
    connectionsBySource: ReadonlyMap<string, Connection[]>
    devices: ConnectionDevice[]
    save: boolean
    discarded: number
    historyLimit: number
    traffic: { uploadTotal: number, downloadTotal: number }
    rate: RateReading
    rateReady: boolean
    samples: RateSample[]
    generation: number
}

export class ConnectionsStore {
    private active = new Map<string, Connection>()
    private readonly closed = new Map<string, Connection>()
    private publishedByID = new Map<string, Connection>()
    private readonly listeners = new Set<() => void>()
    private save = false
    private discarded = 0
    private receivedAt: number | undefined
    private state: ConnectionState
    private readonly clock = new SampleClock()
    private readonly history = new ThroughputHistory()
    private traffic = { uploadTotal: 0, downloadTotal: 0 }
    private rate: RateReading = { up: 0, down: 0, at: 0 }
    private rateReady = false
    private generation = 0
    private lastPublishedAt = -Infinity
    private publication: ReturnType<typeof setTimeout> | undefined
    private frame: number | undefined
    private stale: ReturnType<typeof setTimeout> | undefined

    constructor (private readonly historyLimit = CLOSED_CONNECTION_LIMIT) {
        if (!Number.isInteger(historyLimit) || historyLimit < 0) {
            throw new RangeError('Invalid closed connection limit')
        }
        const connections: Connection[] = []
        this.state = {
            connections, connectionsBySource: new Map([[ALL_CONNECTIONS, connections]]), devices: [],
            save: false, discarded: 0, historyLimit,
            traffic: this.traffic, rate: this.rate, rateReady: false, samples: this.history.samples, generation: 0,
        }
    }

    private cancelPublication () {
        clearTimeout(this.publication)
        this.publication = undefined
        if (this.frame !== undefined) cancelAnimationFrame(this.frame)
        this.frame = undefined
    }

    private schedulePublication () {
        if (this.publication !== undefined || this.frame !== undefined) return
        const delay = Math.max(0, PUBLICATION_INTERVAL_MS - (performance.now() - this.lastPublishedAt))
        this.publication = setTimeout(() => {
            this.publication = undefined
            if (document.hidden) return
            this.frame = requestAnimationFrame(() => {
                this.frame = undefined
                if (!document.hidden) this.publish()
            })
        }, delay)
    }

    private publish () {
        this.cancelPublication()
        this.lastPublishedAt = performance.now()
        const connections: Connection[] = []
        const publishedByID = new Map<string, Connection>()
        const connectionsBySource = new Map<string, Connection[]>()
        // Keep source buttons in stable order without sorting every snapshot.
        for (const device of this.state.devices) connectionsBySource.set(device.key, [])
        // Each record is visited once and shared by All and exactly one source.
        // Counters and filters use these same buckets, never independent counts.
        for (const collection of [this.active, this.closed]) {
            for (const connection of collection.values()) {
                connections.push(connection)
                publishedByID.set(connection.id, connection)
                const bucket = connectionsBySource.get(connection.sourceKey)
                if (bucket) bucket.push(connection)
                else connectionsBySource.set(connection.sourceKey, [connection])
            }
        }
        const devices: ConnectionDevice[] = []
        for (const [key, bucket] of connectionsBySource) {
            if (bucket.length === 0) connectionsBySource.delete(key)
            else devices.push({ key, label: key.startsWith('device:') ? key.slice(7) : key, number: bucket.length })
        }
        for (const [key, bucket] of connectionsBySource) {
            const previous = this.state.connectionsBySource.get(key)
            if (previous?.length === bucket.length && bucket.every((value, index) => value === previous[index])) connectionsBySource.set(key, previous)
        }
        const previousConnections = this.state.connections
        const retainedConnections = previousConnections.length === connections.length && connections.every((value, index) => value === previousConnections[index])
            ? previousConnections
            : connections
        connectionsBySource.set(ALL_CONNECTIONS, retainedConnections)
        const previousDevices = this.state.devices
        const retainedDevices = previousDevices.length === devices.length && devices.every((value, index) => {
            const previous = previousDevices[index]
            return value.key === previous.key && value.label === previous.label && value.number === previous.number
        }) ? previousDevices : devices
        this.publishedByID = publishedByID
        this.state = {
            connections: retainedConnections,
            connectionsBySource,
            devices: retainedDevices,
            save: this.save,
            discarded: this.discarded,
            historyLimit: this.historyLimit,
            traffic: this.traffic,
            rate: this.rate,
            rateReady: this.rateReady,
            samples: this.history.samples,
            generation: this.generation,
        }
        for (const listener of this.listeners) listener()
    }

    private apply (connections: API.Connections[] | null, now: number) {
        if (connections !== null && !Array.isArray(connections)) {
            throw new TypeError('Invalid connection snapshot')
        }
        const elapsed = this.receivedAt === undefined ? 0 : (now - this.receivedAt) / 1000
        this.receivedAt = now
        const next = new Map<string, Connection>()

        for (const connection of connections ?? []) {
            if (next.has(connection.id)) continue
            const previous = this.active.get(connection.id)
            const retained = previous ?? this.closed.get(connection.id)
            const sourceKey = retained !== undefined && retained.metadata?.sourceIP === connection.metadata?.sourceIP &&
                retained.metadata?.type === connection.metadata?.type && (retained.dns === true) === (connection.dns === true)
                ? retained.sourceKey
                : getConnectionSourceKey(connection)
            this.closed.delete(connection.id)
            next.set(connection.id, {
                ...connection,
                sourceKey,
                completed: false,
                uploadSpeed: previous !== undefined && elapsed > 0 ? Math.max(0, connection.upload - previous.upload) / elapsed : 0,
                downloadSpeed: previous !== undefined && elapsed > 0 ? Math.max(0, connection.download - previous.download) / elapsed : 0,
            })
        }

        // Only last snapshot's active connections can have just closed. Retained
        // history is indexed separately and is never reclassified on each tick.
        for (const [id, connection] of this.active) {
            if (next.has(id)) continue
            if (this.save) {
                this.closed.set(id, { ...connection, completed: true, uploadSpeed: 0, downloadSpeed: 0 })
            }
        }
        this.active = next

        while (this.closed.size > this.historyLimit) {
            const oldest = this.closed.keys().next().value as string
            this.closed.delete(oldest)
            this.discarded++
        }
    }

    // Direct feeds remain synchronous for callers that already own sampling.
    feed = (connections: API.Connections[] | null, now = performance.now()) => {
        this.apply(connections, now)
        this.publish()
    }

    // Ingest every observed frame so a short connection can enter retained
    // history even when none of its active frames needs to be painted. Only the
    // derived view is coalesced; there is no queue of old snapshots or renders.
    acceptSnapshot = (snapshot: ConnectionSnapshot) => {
        const timing = this.clock.read(snapshot)
        if (timing === undefined) return
        this.history.shiftTime(timing.shift)
        if (this.receivedAt !== undefined) this.receivedAt += timing.shift
        const resetTotals = snapshot.uploadTotal < this.traffic.uploadTotal || snapshot.downloadTotal < this.traffic.downloadTotal
        if (timing.restart || resetTotals) {
            this.receivedAt = undefined
            this.history.resetBaseline()
        }
        this.rateReady = this.receivedAt !== undefined && performance.now() - timing.at <= RATE_STALE_MS
        const measured = this.history.feed(snapshot, timing.at)
        this.rate = this.rateReady ? measured : { up: 0, down: 0, at: measured.at }
        this.traffic = { uploadTotal: snapshot.uploadTotal, downloadTotal: snapshot.downloadTotal }
        this.generation = snapshot.stream.generation
        this.apply(snapshot.connections, timing.at)
        if (!this.rateReady) this.zeroConnectionRates()
        this.schedulePublication()
        clearTimeout(this.stale)
        this.stale = setTimeout(() => {
            this.stale = undefined
            this.clearRate()
            this.schedulePublication()
        }, Math.max(0, RATE_STALE_MS - Math.max(0, performance.now() - timing.at)))
    }

    private clearRate () {
        this.receivedAt = undefined
        this.history.resetBaseline()
        this.rate = { up: 0, down: 0, at: performance.now() }
        this.rateReady = false
        this.zeroConnectionRates()
    }

    private zeroConnectionRates () {
        for (const [id, connection] of this.active) {
            if (connection.uploadSpeed !== 0 || connection.downloadSpeed !== 0) {
                this.active.set(id, { ...connection, uploadSpeed: 0, downloadSpeed: 0 })
            }
        }
    }

    // A pause/reconnect invalidates measurement continuity, not saved records.
    // Event resets may publish a visible disconnect; cleanup only cancels work.
    resetStream = ({ publish = false }: { publish?: boolean } = {}) => {
        this.cancelPublication()
        clearTimeout(this.stale)
        this.stale = undefined
        this.clock.reset()
        this.clearRate()
        if (publish && !document.hidden) this.schedulePublication()
    }

    clearHistory = () => {
        this.closed.clear()
        this.discarded = 0
        this.publish()
    }

    toggleSave = () => {
        this.save = !this.save
        if (!this.save) this.clearHistory()
        else this.publish()
    }

    getConnection = (id: string) => this.publishedByID.get(id)

    getSnapshot = () => this.state

    subscribe = (listener: () => void) => {
        this.listeners.add(listener)
        return () => { this.listeners.delete(listener) }
    }
}

export function useConnections (reader: ConnectionStreamReader) {
    const store = useMemo(() => new ConnectionsStore(), [reader])
    useLayoutEffect(() => {
        function accept (snapshots: ConnectionSnapshot[]) {
            for (const snapshot of snapshots) store.acceptSnapshot(snapshot)
        }
        function reset () {
            store.resetStream({ publish: true })
        }
        reader.subscribe('data', accept)
        reader.subscribe('reset', reset)
        return () => {
            reader.unsubscribe('data', accept)
            reader.unsubscribe('reset', reset)
            store.resetStream()
        }
    }, [reader, store])
    const state = useSyncExternalStore(store.subscribe, store.getSnapshot)
    return { ...state, feed: store.feed, toggleSave: store.toggleSave, clearHistory: store.clearHistory, getConnection: store.getConnection }
}

