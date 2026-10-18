const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { createRequire } = require('node:module')
const { test } = require('node:test')
const ts = require('typescript')

class PageEvents {
    visibilityState = 'visible'
    listeners = new Map()
    addEventListener(type, listener) {
        const listeners = this.listeners.get(type) || new Set()
        listeners.add(listener)
        this.listeners.set(type, listeners)
    }
    removeEventListener(type, listener) {
        this.listeners.get(type)?.delete(listener)
    }
    emit(type) {
        for (const listener of this.listeners.get(type) || []) listener()
    }
    listenerCount() {
        return [...this.listeners.values()].reduce((count, listeners) => count + listeners.size, 0)
    }
}

function fixture(visibility = 'visible') {
    const document = new PageEvents()
    document.visibilityState = visibility
    const window = new PageEvents()
    const sockets = []
    const timers = new Map()
    let nextTimer = 0
    const clock = { now: 0, reads: 0 }
    class FakeWebSocket extends PageEvents {
        static CLOSING = 2
        readyState = 0
        constructor(url) { super(); this.url = url; sockets.push(this) }
        message(data) {
            for (const listener of this.listeners.get('message') || []) listener({ data })
        }
        close() { this.readyState = 3; this.emit('close') }
        error() {
            for (const listener of this.listeners.get('error') || []) listener(new Error('fixture disconnect'))
        }
    }
    const loaded = new Map()
    function load(filename) {
        if (loaded.has(filename)) return loaded.get(filename).exports
        const compiled = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
            compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, esModuleInterop: true },
        }).outputText
        const module = { exports: {} }
        loaded.set(filename, module)
        const requireModule = createRequire(filename)
        const localRequire = name => name.startsWith('.')
            ? load(path.resolve(path.dirname(filename), name + '.ts'))
            : requireModule(name)
        new Function('require', 'module', 'exports', 'WebSocket', 'setTimeout', 'clearTimeout', 'performance', 'document', 'window', compiled)(
            localRequire, module, module.exports, FakeWebSocket,
            callback => { const id = ++nextTimer; timers.set(id, callback); return id },
            id => timers.delete(id),
            { now: () => { clock.reads++; return clock.now } }, document, window,
        )
        return module.exports
    }
    const { ConnectionStreamReader } = load(path.join(__dirname, 'connection-stream.ts'))
    const { StreamReader } = load(path.join(__dirname, 'streamer.ts'))
    return {
        reader: new ConnectionStreamReader(), StreamReader, sockets, timers, clock, document, window,
        visible(value) { document.visibilityState = value ? 'visible' : 'hidden'; document.emit('visibilitychange') },
        retry() {
            assert.equal(timers.size, 1, 'exactly one retry is scheduled')
            const [id, callback] = timers.entries().next().value
            timers.delete(id)
            callback()
        },
    }
}

const URL_A = 'ws://controller-a.invalid/connections?token=fixture-a'
const URL_B = 'ws://controller-b.invalid/connections?token=fixture-b'
const snapshot = (connections = [], downloadTotal = 0) => JSON.stringify({ connections, uploadTotal: 0, downloadTotal })

test('every received snapshot is delivered once with one shared arrival time and no retained raw history', () => {
    const f = fixture()
    const first = [], second = []
    f.reader.connectWhenVisible(URL_A)
    // These consumers deliberately subscribe after the initial reset event.
    f.reader.subscribe('data', values => first.push(...values))
    f.reader.subscribe('data', values => second.push(...values))
    const socket = f.sockets[0]
    for (const [index, connections] of [[{ id: 'short-A' }], [], [{ id: 'short-B' }], []].entries()) {
        f.clock.now = 1000 + index * 2
        socket.message(snapshot(connections, index))
    }
    assert.deepEqual(first.map(value => value.connections), [[{ id: 'short-A' }], [], [{ id: 'short-B' }], []], 'intermediate observed opens and closes are not coalesced away')
    assert.equal(f.clock.reads, 4, 'arrival time is captured once per message')
    assert.deepEqual(first.map(value => value.stream.receivedAt), [1000, 1002, 1004, 1006])
    assert.equal(new Set(first.map(value => value.stream.generation)).size, 1)
    assert.ok(first[0].stream.generation > 0, 'a late subscriber still receives the current measurement generation')
    first.forEach((value, index) => assert.equal(value, second[index], 'all consumers share the same accepted snapshot envelope'))
    assert.deepEqual(f.reader.buffer(), [])
    f.reader.destory()
})

test('initially hidden pages do not connect; returning keeps subscribers and starts a fresh generation', () => {
    const f = fixture('hidden')
    const received = []
    let resets = 0
    f.reader.subscribe('data', values => received.push(...values))
    f.reader.subscribe('reset', () => { resets++ })
    f.reader.connectWhenVisible(URL_A)
    assert.equal(f.sockets.length, 0)
    f.visible(true)
    const first = f.sockets[0]
    first.message(snapshot([], 1))
    assert.equal(received.length, 1)
    const generation = received[0].stream.generation
    assert.ok(generation > 0)
    const beforePause = resets
    f.visible(false)
    assert.ok(resets > beforePause, 'pausing invalidates a pending display or rate calculation')
    first.message(snapshot([], 999))
    assert.equal(received.length, 1, 'queued old socket messages cannot publish while hidden')
    assert.equal(f.timers.size, 0)
    f.visible(true)
    assert.equal(f.sockets.length, 2)
    f.sockets[1].message(snapshot([], 2))
    assert.equal(received.length, 2, 'pause did not remove data listeners')
    assert.ok(received[1].stream.generation > generation)
    first.message(snapshot([], 999))
    assert.equal(received.length, 2, 'late old messages also stay rejected after resuming')
    f.reader.destory()
})

test('enabled, pagehide, freeze and visibility gates must all permit a connection', () => {
    const f = fixture()
    f.reader.connectWhenVisible(URL_A, false)
    assert.equal(f.sockets.length, 0)
    f.reader.connectWhenVisible(URL_A, true)
    assert.equal(f.sockets.length, 1)
    for (let index = 0; index < 10; index++) {
        f.reader.connectWhenVisible(URL_A, true)
        f.visible(true)
    }
    assert.equal(f.sockets.length, 1, 'unchanged state also deduplicates a connecting socket')
    f.window.emit('pagehide')
    f.reader.connectWhenVisible(URL_A, false)
    f.reader.connectWhenVisible(URL_A, true)
    f.document.emit('resume')
    assert.equal(f.sockets.length, 1, 'enabled changes and resume cannot undo pagehide before pageshow')
    f.window.emit('pageshow')
    assert.equal(f.sockets.length, 2)
    f.document.emit('freeze')
    f.visible(true)
    assert.equal(f.sockets.length, 2, 'visibility alone cannot undo a freeze')
    f.visible(false)
    f.document.emit('resume')
    assert.equal(f.sockets.length, 2, 'resuming while still hidden stays disconnected')
    f.visible(true)
    assert.equal(f.sockets.length, 3)
    f.reader.connectWhenVisible(URL_A, false)
    f.window.emit('pageshow')
    f.document.emit('resume')
    f.visible(true)
    assert.equal(f.sockets.length, 3, 'lifecycle events cannot override the caller disabling the stream')
    f.reader.destory()
})

test('automatic reconnect resets consumers and stale errors or retry callbacks cannot replace the current controller', () => {
    const f = fixture()
    const received = []
    let resets = 0
    f.reader.subscribe('error', () => {})
    f.reader.subscribe('reset', () => { resets++ })
    f.reader.subscribe('data', values => received.push(...values))
    f.reader.connectWhenVisible(URL_A)
    const first = f.sockets[0]
    first.message(snapshot())
    const generation = received[0].stream.generation
    const beforeError = resets
    first.error()
    first.close()
    assert.ok(resets > beforeError)
    f.retry()
    const second = f.sockets[1]
    second.message(snapshot())
    assert.ok(received[1].stream.generation > generation, 'automatic retry supplies a fresh baseline even without a visibility change')
    first.error()
    first.message('{invalid old frame')
    assert.equal(f.timers.size, 0)
    second.close()
    const staleRetry = f.timers.values().next().value
    f.reader.connectWhenVisible(URL_B)
    assert.equal(f.sockets.length, 3)
    staleRetry()
    assert.equal(f.sockets.length, 3, 'a canceled old retry cannot open another socket for the replacement controller')
    assert.equal(f.sockets[2].url, URL_B)
    f.sockets[2].message(snapshot([], 3))
    const currentGeneration = received.at(-1).stream.generation
    second.message(snapshot([], 999))
    assert.equal(received.at(-1).stream.generation, currentGeneration)
    assert.equal(received.at(-1).downloadTotal, 3)
    f.reader.destory()
})

test('hide and complete teardown cancel retry and detach page listeners, including a same-reader remount', () => {
    const f = fixture()
    f.reader.connectWhenVisible(URL_A)
    f.sockets[0].close()
    assert.equal(f.timers.size, 1)
    f.visible(false)
    assert.equal(f.timers.size, 0)
    f.visible(true)
    f.sockets[1].close()
    const staleRetry = f.timers.values().next().value
    f.reader.destory()
    assert.equal(f.timers.size, 0)
    assert.equal(f.document.listenerCount(), 0)
    assert.equal(f.window.listenerCount(), 0)
    f.visible(false)
    f.visible(true)
    f.window.emit('pageshow')
    staleRetry()
    assert.equal(f.sockets.length, 2)
    const received = []
    f.reader.subscribe('data', values => received.push(...values))
    f.reader.connectWhenVisible(URL_A)
    assert.equal(f.sockets.length, 3, 'a same-reader effect remount starts a fresh session')
    assert.equal(f.document.listenerCount(), 3)
    assert.equal(f.window.listenerCount(), 2)
    f.sockets[2].message(snapshot())
    assert.equal(received.length, 1)
    f.sockets[0].message(snapshot([], 999))
    assert.equal(received.length, 1)
    f.reader.destory()
})

test('connection metadata cannot be spoofed and invalid snapshots do not reach consumers', () => {
    const f = fixture()
    const errors = [], received = []
    f.reader.subscribe('error', error => errors.push(error))
    f.reader.subscribe('data', values => received.push(...values))
    f.reader.connectWhenVisible(URL_A)
    const socket = f.sockets[0]
    for (const raw of ['{invalid', 'null', '[]', '{}', snapshot({}, 0), snapshot([], -1), '{"connections":[],"uploadTotal":0,"downloadTotal":1e999}']) socket.message(raw)
    assert.equal(errors.length, 7)
    assert.equal(received.length, 0)
    f.clock.now = 1234
    socket.message(JSON.stringify({ connections: null, uploadTotal: 1, downloadTotal: 2, stream: { generation: -1, receivedAt: -1 } }))
    assert.equal(received.length, 1)
    assert.equal(received[0].connections, null)
    assert.ok(received[0].stream.generation > 0)
    assert.equal(received[0].stream.receivedAt, 1234)
    f.reader.destory()
})

test('ordinary log streams retain their original delivery, buffer and lifecycle behavior', () => {
    const f = fixture('hidden')
    const reader = new f.StreamReader({ bufferLength: 2 })
    const received = []
    let resets = 0
    reader.subscribe('data', values => received.push(...values))
    reader.subscribe('reset', () => { resets++ })
    reader.connect('ws://controller-a.invalid/logs')
    assert.equal(f.sockets.length, 1, 'page visibility is specific to the connection reader')
    for (const payload of ['one', 'two', 'three']) f.sockets[0].message(JSON.stringify({ type: 'info', payload }))
    assert.deepEqual(received, ['one', 'two', 'three'].map(payload => ({ type: 'info', payload })))
    assert.deepEqual(reader.buffer(), received.slice(-2))
    assert.equal(resets, 0)
    assert.equal(f.clock.reads, 0, 'ordinary messages are not wrapped or timestamped')
    assert.equal(f.document.listenerCount(), 0)
    reader.destory()
})
