const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { createRequire } = require('node:module')
const { test } = require('node:test')
const ts = require('typescript')

const MIB = 1024 * 1024
const compiledSources = new Map()

// Timers and animation frames are separate queues: messages may arrive while
// the browser has not painted, but advancing time never manufactures a paint.
class Scheduler {
    now = 100_000
    nextID = 1
    timers = new Map()
    frames = new Map()

    setTimeout = (callback, delay = 0) => {
        const id = this.nextID++
        this.timers.set(id, { callback, at: this.now + Math.max(0, delay) })
        return id
    }

    clearTimeout = id => { this.timers.delete(id) }
    requestAnimationFrame = callback => {
        const id = this.nextID++
        this.frames.set(id, callback)
        return id
    }

    cancelAnimationFrame = id => { this.frames.delete(id) }

    advanceTo(now) {
        assert.ok(now >= this.now, 'the browser clock is monotonic')
        let runs = 0
        while (true) {
            const next = [...this.timers].filter(([, task]) => task.at <= now)
                .sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0]
            if (!next) break
            assert.ok(++runs < 1000, 'a timer cannot spin indefinitely')
            this.now = next[1].at
            this.timers.delete(next[0])
            next[1].callback()
        }
        this.now = now
    }

    paint() {
        this.advanceTo(this.now)
        const frames = [...this.frames.values()]
        this.frames.clear()
        for (const callback of frames) callback(this.now)
    }

    get pending() { return this.timers.size + this.frames.size }
}

function harness() {
    const scheduler = new Scheduler()
    const document = { hidden: false }
    const effects = []
    const hooks = {}
    const react = {
        useMemo: create => create(),
        useLayoutEffect: effect => { effects.push(effect) },
        useSyncExternalStore: (subscribe, getSnapshot) => {
            hooks.subscribe = subscribe
            hooks.getSnapshot = getSnapshot
            return getSnapshot()
        },
    }
    const sources = new Map()
    function loadSource(relative) {
        const filename = path.resolve(__dirname, relative)
        if (sources.has(filename)) return sources.get(filename).exports
        if (!compiledSources.has(filename)) {
            compiledSources.set(filename, ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
                compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, esModuleInterop: true },
            }).outputText)
        }
        const module = { exports: {} }
        sources.set(filename, module)
        const requireModule = createRequire(filename)
        const localRequire = name => {
            if (name === 'react') return react
            const local = path.resolve(path.dirname(filename), name + '.ts')
            return name.startsWith('.') && fs.existsSync(local) ? loadSource(local) : requireModule(name)
        }
        new Function('require', 'module', 'exports', 'setTimeout', 'clearTimeout',
            'requestAnimationFrame', 'cancelAnimationFrame', 'document', 'performance', compiledSources.get(filename))(
            localRequire, module, module.exports, scheduler.setTimeout, scheduler.clearTimeout,
            scheduler.requestAnimationFrame, scheduler.cancelAnimationFrame, document, { now: () => scheduler.now },
        )
        return module.exports
    }
    const { ConnectionsStore, useConnections } = loadSource('store.ts')
    return {
        scheduler, document, ConnectionsStore,
        mount(reader) {
            useConnections(reader)
            const cleanups = effects.splice(0).map(effect => effect())
            return { ...hooks, unmount: () => { for (const cleanup of cleanups) cleanup() } }
        },
    }
}

function connection(id, bytes = 0) {
    return {
        id,
        metadata: { sourceIP: '192.0.2.1', sourcePort: '50000', host: id + '.test', destinationIP: '198.51.100.1', destinationPort: '443', network: 'tcp', type: 'Socks5' },
        upload: bytes, download: bytes, start: '2026-01-01T00:00:00Z', chains: ['DIRECT'], rule: 'Match', rulePayload: '',
    }
}

function snapshot(sequence, receivedAt, overrides = {}) {
    const bytes = (sequence - 1) * MIB
    return {
        uploadTotal: bytes, downloadTotal: bytes, connections: [connection('selected', bytes)],
        sampledAt: (sequence - 1) * 1000, sequence,
        ...overrides,
        stream: { generation: 1, receivedAt, ...overrides.stream },
    }
}

function legacySnapshot(sequence, receivedAt, overrides = {}) {
    const { sampledAt, sequence: ignored, ...legacy } = snapshot(sequence, receivedAt, overrides)
    return legacy
}

class Reader {
    listeners = new Map()
    subscribe(event, listener) {
        if (!this.listeners.has(event)) this.listeners.set(event, new Set())
        this.listeners.get(event).add(listener)
    }

    unsubscribe(event, listener) { this.listeners.get(event)?.delete(listener) }
    emit(event, ...args) { for (const listener of this.listeners.get(event) ?? []) listener(...args) }
    count(event) { return this.listeners.get(event)?.size ?? 0 }
}

test('one-second source samples keep their real rate when delivered in 2–5 ms bursts', () => {
    for (const callbackInterval of [2, 3, 5]) {
        const { scheduler, ConnectionsStore } = harness()
        const store = new ConnectionsStore()
        const publications = []
        store.subscribe(() => publications.push(store.getSnapshot()))
        for (let sequence = 1; sequence <= 30; sequence++) {
            scheduler.advanceTo(100_000 + (sequence - 1) * callbackInterval)
            store.acceptSnapshot(snapshot(sequence, scheduler.now))
        }
        assert.equal(publications.length, 0, 'ingestion does not synchronously publish every message')
        scheduler.paint()
        const state = store.getSnapshot()
        assert.equal(publications.length, 1)
        assert.equal(state.rateReady, true)
        assert.equal(state.rate.up, MIB)
        assert.equal(state.rate.down, MIB)
        assert.equal(state.connections[0].downloadSpeed, MIB, 'row and chart share the source interval')
        assert.equal(state.connections[0].uploadSpeed, MIB)
        assert.equal(state.traffic.downloadTotal, 29 * MIB)
        assert.equal(state.samples.length, 29, 'all measured intervals reach history')
        assert.ok(state.samples.every(sample => sample.up === MIB && sample.down === MIB && sample.span === 1000))
        store.resetStream()
    }
})

test('one pending publication retains every observed short connection in closed history', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.toggleSave()
    let publications = 0
    store.subscribe(() => { publications++ })
    const expected = []
    for (let index = 0; index < 20; index++) {
        const id = 'short-' + index
        expected.push(id)
        scheduler.advanceTo(100_000 + index * 6)
        store.acceptSnapshot(snapshot(index * 2 + 1, scheduler.now, { connections: [connection(id)] }))
        scheduler.advanceTo(scheduler.now + 3)
        store.acceptSnapshot(snapshot(index * 2 + 2, scheduler.now, { connections: [] }))
        assert.equal(store.getConnection(id), undefined, 'selection reads the last published view')
        assert.ok(scheduler.pending <= 2, 'only a stale timer and one publication task can be pending')
        assert.ok(scheduler.frames.size <= 1)
    }
    assert.equal(publications, 0)
    scheduler.paint()
    assert.equal(publications, 1)
    assert.deepEqual(store.getSnapshot().connections.map(value => value.id), expected)
    assert.ok(store.getSnapshot().connections.every(value => value.completed))

    const publishedAt = scheduler.now
    store.acceptSnapshot(snapshot(41, scheduler.now, { connections: [connection('latest')] }))
    scheduler.advanceTo(publishedAt + 99)
    scheduler.paint()
    assert.equal(publications, 1, 'a following update respects the 100 ms publication interval')
    scheduler.advanceTo(publishedAt + 100)
    scheduler.paint()
    assert.equal(publications, 2)
    assert.equal(store.getSnapshot().connections[0].id, 'latest')
    assert.equal(store.getSnapshot().connections.length, expected.length + 1)
    store.resetStream()
})

test('duplicate and out-of-order source sequences do not overwrite data or the next baseline', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.acceptSnapshot(snapshot(10, scheduler.now))
    scheduler.paint()
    scheduler.advanceTo(101_000)
    store.acceptSnapshot(snapshot(11, scheduler.now))
    scheduler.paint()
    const published = store.getSnapshot()
    const pending = scheduler.pending
    for (const sequence of [11, 9, 10]) {
        scheduler.advanceTo(scheduler.now + 2)
        store.acceptSnapshot(snapshot(sequence, scheduler.now, {
            uploadTotal: 1e12, downloadTotal: 1e12, connections: [connection('outdated', 1e12)],
        }))
    }
    scheduler.paint()
    assert.equal(store.getSnapshot(), published)
    assert.equal(store.getConnection('outdated'), undefined)
    assert.equal(scheduler.pending, pending, 'ignored frames schedule no work')
    scheduler.advanceTo(102_000)
    store.acceptSnapshot(snapshot(12, scheduler.now))
    scheduler.paint()
    assert.equal(store.getSnapshot().rate.down, MIB)
    assert.equal(store.getSnapshot().connections[0].downloadSpeed, MIB)
    store.resetStream()
})

test('resume keeps saved IDs and history while the first new generation establishes a baseline', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.toggleSave()
    store.acceptSnapshot(snapshot(1, scheduler.now, { connections: [connection('selected'), connection('closed')] }))
    scheduler.advanceTo(101_000)
    store.acceptSnapshot(snapshot(2, scheduler.now))
    scheduler.paint()
    const samples = structuredClone(store.getSnapshot().samples)
    const selectedID = 'selected'
    const closed = store.getConnection('closed')
    assert.equal(closed.completed, true)
    assert.equal(store.getSnapshot().rateReady, true)
    store.resetStream()
    assert.equal(scheduler.pending, 0)

    scheduler.advanceTo(106_000)
    store.acceptSnapshot(snapshot(100, scheduler.now, {
        uploadTotal: 1e9, downloadTotal: 1e9,
        connections: [connection(selectedID, 1e9)], stream: { generation: 2 },
    }))
    scheduler.paint()
    const first = store.getSnapshot()
    assert.equal(first.generation, 2)
    assert.equal(first.rateReady, false)
    assert.equal(first.rate.down, 0)
    assert.equal(first.connections[0].downloadSpeed, 0)
    assert.equal(first.save, true)
    assert.equal(store.getConnection(selectedID).id, selectedID)
    assert.equal(store.getConnection('closed'), closed)
    assert.deepEqual(first.samples, samples)

    scheduler.advanceTo(107_000)
    store.acceptSnapshot(snapshot(101, scheduler.now, {
        uploadTotal: 1e9 + MIB, downloadTotal: 1e9 + MIB,
        connections: [connection(selectedID, 1e9 + MIB)], stream: { generation: 2 },
    }))
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, true)
    assert.equal(store.getSnapshot().rate.down, MIB)
    assert.notEqual(store.getSnapshot().samples.at(-1).segment, samples[0].segment)
    store.resetStream()
})

test('cleanup reset cancels both timer and animation-frame publication stages', () => {
    for (const stage of ['timer', 'frame']) {
        const { scheduler, ConnectionsStore } = harness()
        const store = new ConnectionsStore()
        let publications = 0
        store.subscribe(() => { publications++ })
        store.acceptSnapshot(snapshot(1, scheduler.now))
        if (stage === 'frame') scheduler.advanceTo(scheduler.now)
        assert.equal(scheduler.frames.size, stage === 'frame' ? 1 : 0)
        store.resetStream()
        assert.equal(scheduler.pending, 0, stage)
        scheduler.advanceTo(120_000)
        scheduler.paint()
        assert.equal(publications, 0, stage + ' cannot publish after cleanup')
    }
})

test('the real hook reset subscription clears visible rates; unmount cancels and unsubscribes', () => {
    const fixture = harness()
    const { scheduler } = fixture
    const reader = new Reader()
    const mounted = fixture.mount(reader)
    let publications = 0
    const unsubscribe = mounted.subscribe(() => { publications++ })
    assert.equal(reader.count('data'), 1)
    assert.equal(reader.count('reset'), 1)
    reader.emit('data', [snapshot(1, scheduler.now)])
    scheduler.paint()
    scheduler.advanceTo(101_000)
    reader.emit('data', [snapshot(2, scheduler.now)])
    scheduler.paint()
    assert.equal(mounted.getSnapshot().rateReady, true)
    assert.equal(mounted.getSnapshot().connections[0].downloadSpeed, MIB)

    const beforeReset = publications
    reader.emit('reset')
    scheduler.advanceTo(101_100)
    scheduler.paint()
    assert.equal(publications, beforeReset + 1, 'visible disconnect publishes an unavailable rate once')
    assert.equal(mounted.getSnapshot().rateReady, false)
    assert.equal(mounted.getSnapshot().rate.down, 0)
    assert.equal(mounted.getSnapshot().connections[0].downloadSpeed, 0)

    reader.emit('data', [snapshot(3, scheduler.now, { stream: { generation: 2 } })])
    const beforeUnmount = publications
    mounted.unmount()
    assert.equal(reader.count('data'), 0)
    assert.equal(reader.count('reset'), 0)
    assert.equal(scheduler.pending, 0)
    reader.emit('data', [snapshot(4, scheduler.now)])
    reader.emit('reset')
    scheduler.advanceTo(121_100)
    scheduler.paint()
    assert.equal(publications, beforeUnmount, 'even an independent observer sees no post-unmount publication')
    unsubscribe()
})

test('hidden reset cancels queued work without scheduling a background publication', () => {
    const { scheduler, document, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    let publications = 0
    store.subscribe(() => { publications++ })
    store.acceptSnapshot(snapshot(1, scheduler.now))
    scheduler.advanceTo(scheduler.now)
    assert.equal(scheduler.frames.size, 1)
    document.hidden = true
    store.resetStream({ publish: true })
    assert.equal(scheduler.pending, 0)
    scheduler.advanceTo(120_000)
    scheduler.paint()
    assert.equal(publications, 0)
})

test('legacy bursts suppress fabricated rates and recover at the normal sampling cadence', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.acceptSnapshot(legacySnapshot(1, scheduler.now))
    scheduler.paint()
    scheduler.advanceTo(101_000)
    store.acceptSnapshot(legacySnapshot(2, scheduler.now))
    scheduler.paint()
    assert.equal(store.getSnapshot().rate.down, MIB)
    const measuredSegment = store.getSnapshot().samples.at(-1).segment

    for (let sequence = 3; sequence <= 22; sequence++) {
        scheduler.advanceTo(scheduler.now + 3)
        store.acceptSnapshot(legacySnapshot(sequence, scheduler.now))
        assert.ok(store.getConnection('selected').downloadSpeed <= MIB, 'a pending view never exposes a burst spike')
    }
    scheduler.advanceTo(101_100)
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, false)
    assert.equal(store.getSnapshot().rate.down, 0)
    assert.equal(store.getSnapshot().connections[0].downloadSpeed, 0)
    assert.ok(store.getSnapshot().samples.every(sample => sample.down <= MIB))

    scheduler.advanceTo(102_060)
    store.acceptSnapshot(legacySnapshot(23, scheduler.now))
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, true)
    assert.equal(store.getSnapshot().rate.down, MIB)
    assert.equal(store.getSnapshot().connections[0].downloadSpeed, MIB)
    assert.notEqual(store.getSnapshot().samples.at(-1).segment, measuredSegment)
    store.resetStream()
})

test('a better source-clock offset translates history without changing measured rates or intervals', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.acceptSnapshot(snapshot(1, scheduler.now))
    scheduler.paint()
    scheduler.advanceTo(101_000)
    store.acceptSnapshot(snapshot(2, scheduler.now))
    scheduler.paint()
    const original = structuredClone(store.getSnapshot().samples)
    assert.equal(original.length, 1)

    // The next sample takes 500 ms less time to reach the browser. Source time
    // still advances one full second, so only its position on the chart moves.
    scheduler.advanceTo(101_500)
    store.acceptSnapshot(snapshot(3, scheduler.now))
    scheduler.paint()
    const state = store.getSnapshot()
    assert.equal(state.rate.down, MIB)
    assert.equal(state.connections[0].downloadSpeed, MIB)
    assert.deepEqual(state.samples[0], { ...original[0], at: original[0].at - 500 })
    assert.equal(state.samples[1].at - state.samples[0].at, 1000)
    assert.equal(state.samples[1].span, 1000)
    assert.equal(state.samples[1].down, MIB)
    store.resetStream()
})

test('known delayed samples keep their measurements in history without showing stale live rates', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    // Source time 1 s establishes the offset at browser time 100 s.
    store.acceptSnapshot(snapshot(2, scheduler.now))
    scheduler.paint()

    // A blocked event loop advances the clock before it dispatches queued tasks.
    // Deliver the message before the overdue stale timer: running advanceTo here
    // would test only the existing silence reset, not delayed-message handling.
    scheduler.now = 130_000
    store.acceptSnapshot(snapshot(3, scheduler.now)) // source 2 s, now 29 s old
    scheduler.paint()
    const delayed = store.getSnapshot()
    assert.equal(delayed.rateReady, false)
    assert.equal(delayed.rate.up, 0)
    assert.equal(delayed.rate.down, 0, 'a known old measurement is not the current throughput')
    assert.equal(store.getConnection('selected').uploadSpeed, 0)
    assert.equal(store.getConnection('selected').downloadSpeed, 0)
    assert.equal(delayed.traffic.downloadTotal, 2 * MIB, 'the connection snapshot is still ingested')
    assert.equal(delayed.samples.length, 1)
    assert.equal(delayed.samples[0].at, 101_000, 'history keeps the real mapped sampling time')
    assert.equal(delayed.samples[0].span, 1000)
    assert.equal(delayed.samples[0].up, MIB)
    assert.equal(delayed.samples[0].down, MIB, 'suppressing a live reading does not erase the measurement')

    // A fresh source-31 s snapshot ends the long gap; the following source-32 s
    // snapshot has a complete interval and must recover the correct live rate.
    scheduler.advanceTo(130_002)
    store.acceptSnapshot(snapshot(32, scheduler.now))
    scheduler.advanceTo(130_100)
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, false)
    assert.equal(store.getSnapshot().rate.down, 0)
    assert.equal(store.getConnection('selected').downloadSpeed, 0)
    scheduler.advanceTo(131_000)
    store.acceptSnapshot(snapshot(33, scheduler.now))
    scheduler.paint()
    const recovered = store.getSnapshot()
    assert.equal(recovered.rateReady, true)
    assert.equal(recovered.rate.up, MIB)
    assert.equal(recovered.rate.down, MIB)
    assert.equal(store.getConnection('selected').uploadSpeed, MIB)
    assert.equal(store.getConnection('selected').downloadSpeed, MIB)
    assert.equal(recovered.samples.at(-1).span, 1000)
    assert.equal(recovered.samples.at(-1).down, MIB)
    store.resetStream()
})

test('a nine-second-old sample expires after its remaining second, including processing delay', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.acceptSnapshot(snapshot(2, scheduler.now)) // source 1 s maps to browser 100 s
    scheduler.paint()

    // Dispatch the delayed message before the old timer after the event loop
    // resumes. Its receive timestamp precedes ingestion by 500 ms, so that
    // processing time must also count towards the source sample's lifetime.
    scheduler.now = 110_000
    store.acceptSnapshot(snapshot(3, 109_500)) // source 2 s maps to browser 101 s
    scheduler.paint()
    const measured = store.getSnapshot()
    assert.equal(measured.rateReady, true)
    assert.equal(measured.rate.up, MIB)
    assert.equal(measured.rate.down, MIB)
    assert.equal(store.getConnection('selected').uploadSpeed, MIB)
    assert.equal(store.getConnection('selected').downloadSpeed, MIB)
    const history = structuredClone(measured.samples)
    assert.equal(history[0].at, 101_000)

    scheduler.advanceTo(110_999)
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, true, 'the measurement still has 1 ms of its lifetime left')
    assert.equal(store.getSnapshot().rate.down, MIB)

    scheduler.advanceTo(111_000)
    scheduler.paint()
    const expired = store.getSnapshot()
    assert.equal(expired.rateReady, false, 'delivery and processing do not restart the ten-second lifetime')
    assert.equal(expired.rate.up, 0)
    assert.equal(expired.rate.down, 0)
    assert.equal(store.getConnection('selected').uploadSpeed, 0)
    assert.equal(store.getConnection('selected').downloadSpeed, 0)
    assert.equal(expired.traffic.downloadTotal, 2 * MIB)
    assert.deepEqual(expired.samples, history, 'live-rate expiry preserves the historical measurement')
    store.resetStream()
})

test('a silent stream stops presenting its last throughput and needs a fresh baseline', () => {
    const { scheduler, ConnectionsStore } = harness()
    const store = new ConnectionsStore()
    store.acceptSnapshot(snapshot(1, scheduler.now))
    scheduler.paint()
    scheduler.advanceTo(101_000)
    store.acceptSnapshot(snapshot(2, scheduler.now))
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, true)
    scheduler.advanceTo(111_000)
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, false)
    assert.equal(store.getSnapshot().rate.down, 0)
    scheduler.advanceTo(111_100)
    store.acceptSnapshot(snapshot(3, scheduler.now))
    scheduler.paint()
    assert.equal(store.getSnapshot().rateReady, false, 'the first frame after silence only establishes continuity')
    assert.equal(store.getSnapshot().rate.down, 0)
    store.resetStream()
})
