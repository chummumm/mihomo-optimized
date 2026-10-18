#!/usr/bin/env node
// Exercise the production chart, including its real React/store/Canvas code.
// Playwright's clock advances actual application timers and animation frames;
// this is not an independent implementation of the chart's motion algorithm.
const assert = require('node:assert/strict')
const fs = require('node:fs')
const http = require('node:http')
const path = require('node:path')
const { WebSocketServer } = require('ws')
const { chromium } = require('playwright')

const dist = path.resolve(process.env.UI_DIST_DIR || path.join(__dirname, '../dist'))
const output = path.resolve(process.env.THROUGHPUT_EDGES_OUTPUT_DIR || '/tmp/mihomo-throughput-edges')
const MiB = 1024 * 1024
const rates = [1, 1.25, 2.5, 3, 1.5, 0.5, 1, 2]
const config = { port: 0, 'socks-port': 0, 'mixed-port': 0, 'redir-port': 0, mode: 'rule', 'log-level': 'info', 'allow-lan': false }
const errors = []
const observations = []
const sockets = new Set()
let browser
let page
let current
let sequence = 0
let sourceTime = 0
let download = 0
let upload = 0
let opened = 0
let elapsed = 0
let anchor = 0
let screenshots = []

function sample(at) {
    const span = at - sourceTime
    assert.ok(span >= 0, 'fixture source sampling time never moves backwards')
    download += rates[Math.floor(at / 1000) % rates.length] * MiB * span / 1000
    upload += 0.5 * MiB * span / 1000
    sourceTime = at
    current = {
        sampledAt: 100000 + at, sequence: ++sequence, uploadTotal: upload, downloadTotal: download,
        connections: [{
            id: 'edge-connection',
            metadata: { network: 'tcp', type: 'Socks5', host: 'throughput-edges.test', sourceIP: '192.0.2.10', sourcePort: '20000', destinationIP: '198.51.100.1', destinationPort: '443' },
            upload, download, start: '2026-01-01T00:00:00Z', chains: ['DIRECT'], rule: 'Match', rulePayload: '',
        }],
    }
    return current
}
sample(0)

const server = http.createServer((req, res) => {
    const pathname = new URL(req.url, 'http://127.0.0.1').pathname
    const api = {
        '/version': { version: 'isolated-throughput-edges-test', premium: false }, '/configs': config,
        '/connections': current, '/rules': { rules: [] },
        '/providers/proxies': { providers: {} }, '/providers/rules': { providers: {} },
        '/proxies': { proxies: { GLOBAL: { name: 'GLOBAL', type: 'Selector', now: 'DIRECT', history: [], all: ['DIRECT'] }, DIRECT: { name: 'DIRECT', type: 'Direct', history: [] } } },
    }
    if (api[pathname]) {
        res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' })
        res.end(JSON.stringify(api[pathname])); return
    }
    const file = path.resolve(dist, '.' + (pathname === '/' ? '/index.html' : pathname))
    if (!file.startsWith(dist + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return }
    const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml', '.png': 'image/png', '.webmanifest': 'application/manifest+json' }
    res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream' })
    fs.createReadStream(file).pipe(res)
})
const wss = new WebSocketServer({ server })
wss.on('connection', (socket, req) => {
    if (new URL(req.url, 'http://127.0.0.1').pathname !== '/connections') { socket.close(); return }
    opened++
    sockets.add(socket)
    socket.send(JSON.stringify(current))
    socket.on('close', () => sockets.delete(socket))
})

async function until(predicate, label) {
    const deadline = Date.now() + 10000
    while (Date.now() < deadline) {
        if (await predicate()) return
        await new Promise(resolve => setTimeout(resolve, 10))
    }
    assert.fail(label)
}

async function received(expected = sequence) {
    await until(() => page.evaluate(value => window.throughputEdgesFixture.received === value, expected), 'the real local WebSocket delivers the expected source snapshot')
}

async function send(at) {
    const snapshot = sample(at)
    assert.equal(sockets.size, 1, 'the fixture sends to one statistics socket')
    for (const socket of sockets) socket.send(JSON.stringify(snapshot))
    await received(snapshot.sequence)
}

async function screenshot(name, sides = false) {
    const box = await page.locator('canvas.connections-chart').boundingBox()
    assert.ok(box && box.width > 200 && box.height > 40, 'the real chart has visible geometry')
    if (sides) {
        const width = Math.min(180, box.width / 3)
        for (const [side, x] of [['left', box.x], ['right', box.x + box.width - width]]) {
            await page.screenshot({ path: path.join(output, `${name}-${side}.png`), clip: { x, y: box.y, width, height: box.height }, animations: 'allow' })
        }
    } else {
        await page.screenshot({ path: path.join(output, `${name}.png`), clip: box, animations: 'allow' })
    }
}

async function advanceTo(target) {
    assert.ok(target >= elapsed, 'fixture display time moves forwards')
    while (screenshots.length && screenshots[0] <= target) {
        const at = screenshots.shift()
        await page.clock.runFor(at - elapsed)
        elapsed = at
        await screenshot(`edge-${String(at).padStart(6, '0')}`, true)
    }
    await page.clock.runFor(target - elapsed)
    elapsed = target
}

async function phase(name) {
    await page.evaluate(name => { window.throughputEdgesFixture.phase = name }, name)
}

// Decode only Canvas operations which the production renderer actually issued.
// Geometric continuity is checked by translating consecutive frames, without
// calling the chart's own interpolation or clipping helpers from the test.
function segments(frame, key = 'down') {
    const result = []
    const width = key === 'down' ? 2.2 : 1.8
    let section = -1
    for (const stroke of frame.strokes.filter(stroke => Math.abs(stroke.width - width) < 0.01)) {
        let point
        for (const [command, ...args] of stroke.operations) {
            if (command === 'moveTo') { point = args.slice(0, 2); section++ }
            else if (command === 'lineTo' && point) {
                const end = args.slice(0, 2)
                if (end[0] > point[0] + 1e-8) result.push({ start: point, end, section })
                point = end
            } else if (command === 'quadraticCurveTo' && point) {
                const end = args.slice(2, 4)
                if (end[0] > point[0] + 1e-8) result.push({ start: point, control: args.slice(0, 2), end, section })
                point = end
            }
        }
    }
    return result
}

function assertFillClosure(frame, label) {
    assert.equal(frame.fills.length, 2, `${label}: both series have an independent filled area`)
    for (const fill of frame.fills) {
        const lastStart = fill.operations.findLastIndex(operation => operation[0] === 'moveTo')
        const area = fill.operations.slice(lastStart)
        const closing = area.slice(-4)
        assert.equal(closing[0][0], 'lineTo', `${label}: live fill extends past the visible plot`)
        assert.equal(closing[1][0], 'lineTo', `${label}: live fill closes at the baseline`)
        assert.ok(closing[0][1] > frame.width && closing[1][1] === closing[0][1], `${label}: the active fill's vertical closing edge is outside the clip`)
        assert.equal(closing[3][0], 'closePath')
    }
    for (const stroke of frame.strokes) assert.ok(!stroke.operations.some(operation => operation[0] === 'closePath'), `${label}: area closing edges are never stroked`)
}

function latestValueError(frame, tick) {
    const heads = frame.heads.filter(head => Math.abs(head.radius - 3) < 1e-6)
    if (heads.length < 2) return Infinity
    const baseline = frame.height - (frame.height < 120 ? 4 : 20)
    // The constant upload rate is an independent reference for the actual
    // current y scale. No chart helper or internally exported scale is used.
    const expected = baseline - (baseline - heads[1].y) * rates[tick % rates.length] / 0.5
    return Math.abs(heads[0].y - expected)
}

async function assertNewest(tick, deliveredAt) {
    const frames = await page.evaluate(value => window.throughputEdgesFixture.frames.filter(frame => frame.sequence === value), sequence)
    const frame = frames.find(frame => latestValueError(frame, tick) <= 0.05)
    assert.ok(frame, `source sample ${tick}: latest measured value reaches the real chart without a fixed display buffer`)
    const latency = frame.at - anchor - deliveredAt
    assert.ok(latency >= 0 && latency <= 128, `source sample ${tick}: newest value is visible within the normal UI publication interval (${latency} ms)`)
    return latency
}

function yAt(geometry, x) {
    const piece = geometry.find(piece => x >= piece.start[0] - 1e-7 && x <= piece.end[0] + 1e-7)
    if (!piece) return undefined
    if (!piece.control) {
        const t = (x - piece.start[0]) / (piece.end[0] - piece.start[0])
        return piece.start[1] + (piece.end[1] - piece.start[1]) * t
    }
    const quadratic = (axis, t) => (1 - t) ** 2 * piece.start[axis] + 2 * t * (1 - t) * piece.control[axis] + t ** 2 * piece.end[axis]
    let low = 0
    let high = 1
    for (let iteration = 0; iteration < 32; iteration++) {
        const middle = (low + high) / 2
        if (quadratic(0, middle) < x) low = middle
        else high = middle
    }
    return quadratic(1, (low + high) / 2)
}

function metrics(frames, label, residualLimit) {
    const usable = frames.filter(frame => frame.phase === label && segments(frame).length > 5)
    assert.ok(usable.length >= 40, `${label}: many successive real Canvas frames were observed`)
    let maxHeadStep = 0
    let maxHeadVelocity = 0
    let maxHeadInsetError = 0
    let maxLeftResidual = 0
    let compared = 0
    for (let index = 0; index < usable.length; index++) {
        const frame = usable[index]
        const geometry = segments(frame)
        assertFillClosure(frame, label)
        assert.ok(frame.clips.some(clip => clip.some(([command, x, , width]) => command === 'rect' && Math.abs(x) < 0.01 && Math.abs(width - frame.width) < 0.01)), `${label}: the plot is clipped to the real viewport`)
        assert.ok(yAt(geometry, 0) !== undefined && yAt(geometry, 2) !== undefined, `${label}: retiring an old point never exposes an empty strip at the left edge`)
        const heads = frame.heads.filter(head => Math.abs(head.radius - 3) < 1e-6)
        assert.ok(heads.length >= 2, `${label}: both measured curves retain their drawing endpoints`)
        for (const head of heads) {
            const error = Math.abs(head.x - (frame.width - 3))
            maxHeadInsetError = Math.max(maxHeadInsetError, error)
            assert.ok(error <= 0.15, `${label}: the latest-value head stays at a stable right edge (${error.toFixed(3)} px inset error)`)
        }
        for (const [index, key] of ['down', 'up'].entries()) {
            const onCurve = yAt(segments(frame, key), heads[index].x)
            assert.ok(onCurve !== undefined && Math.abs(onCurve - heads[index].y) <= 0.05, `${label}: the ${key} endpoint sits on its actual clipped curve`)
        }
        if (index === 0) continue
        const previous = usable[index - 1]
        const dt = frame.at - previous.at
        if (dt <= 0 || dt > 80 || frame.width !== previous.width) continue
        const previousHeads = previous.heads.filter(head => Math.abs(head.radius - 3) < 1e-6)
        for (let head = 0; head < Math.min(2, heads.length, previousHeads.length); head++) {
            const step = Math.abs(heads[head].x - previousHeads[head].x)
            maxHeadStep = Math.max(maxHeadStep, step)
            maxHeadVelocity = Math.max(maxHeadVelocity, step * 60000 / frame.width / dt)
            // Retain a separate movement bound to diagnose any future drift.
            assert.ok(step <= 2.2 * frame.width * dt / 60000 + 0.15, `${label}: endpoint movement is continuous (${step.toFixed(3)} px in ${dt.toFixed(1)} ms)`)
        }
        const beforeGeometry = segments(previous)
        const shift = (frame.width - 3) * dt / 60000
        for (const x of [0, 1, 4, 8, 14, 24]) {
            const before = yAt(beforeGeometry, x + shift)
            const after = yAt(geometry, x)
            assert.ok(before !== undefined && after !== undefined, `${label}: left-edge curve remains drawable across expiration`)
            const residual = Math.abs(after - before)
            maxLeftResidual = Math.max(maxLeftResidual, residual)
            assert.ok(residual <= residualLimit, `${label}: historical edge shape is continuous (${residual.toFixed(3)} px residual at x=${x})`)
            compared++
        }
    }
    assert.ok(compared >= 120, `${label}: continuity was compared across sample boundaries`)
    return { label, frames: usable.length, comparisons: compared, maxHeadStepPx: maxHeadStep, maxHeadInsetErrorPx: maxHeadInsetError, maxHeadSpeedInWindowsPerMinute: maxHeadVelocity, maxLeftResidualPx: maxLeftResidual }
}

function bytesPerSecond(text) {
    const match = text.match(/([\d.]+)\s*(B|KiB|MiB|GiB|TiB)\/s/)
    assert.ok(match, 'a rate is displayed with an explicit byte unit: ' + text)
    return Number(match[1]) * 1024 ** ['B', 'KiB', 'MiB', 'GiB', 'TiB'].indexOf(match[2])
}

async function persist() {
    if (!page || page.isClosed()) return
    const state = await page.evaluate(() => window.throughputEdgesFixture.export())
    fs.writeFileSync(path.join(output, 'geometry.json'), JSON.stringify({ anchor, observations, errors, ...state }))
}

async function inspectPixels(label) {
    const result = await page.evaluate(() => {
        const canvas = document.querySelector('canvas.connections-chart')
        const ctx = canvas.getContext('2d')
        const frame = window.throughputEdgesFixture.latest
        const heads = frame.heads.filter(head => Math.abs(head.radius - 3) < 1e-6)
        const ratio = Math.min(2, devicePixelRatio)
        const aboveHead = (Math.min(...heads.map(head => head.y)) - 13) * ratio
        let strayPixels = 0
        // A normal pulse reaches at most 12 CSS px from the current head.
        // A bright, full-height edge above it is not part of the live graphic.
        for (let x = canvas.width - 3; x < canvas.width; x++) {
            const pixels = ctx.getImageData(x, 0, 1, canvas.height).data
            for (let y = 0; y < Math.max(0, aboveHead); y++) {
                const index = y * 4
                if (pixels[index + 3] > 60 && pixels[index] < 130 && pixels[index + 2] > 130) strayPixels++
            }
        }
        return { width: canvas.clientWidth, height: canvas.clientHeight, backingWidth: canvas.width, backingHeight: canvas.height, dpr: devicePixelRatio, heads: heads.length, strayPixels }
    })
    assert.equal(result.heads, 2, `${label}: both latest-value markers are visible`)
    assert.equal(result.strayPixels, 0, `${label}: the physical right edge has no bright residue above the current data`)
    return { label, ...result }
}

async function main() {
    fs.mkdirSync(output, { recursive: true })
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
    const port = server.address().port
    browser = await chromium.launch({
        headless: true,
        executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || undefined,
        args: process.env.PLAYWRIGHT_CHROMIUM_ARGS ? JSON.parse(process.env.PLAYWRIGHT_CHROMIUM_ARGS) : [],
    })
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', serviceWorkers: 'block', reducedMotion: 'no-preference' })
    await context.route('**/*', route => {
        const url = new URL(route.request().url())
        return url.hostname === '127.0.0.1' && url.port === String(port) ? route.continue() : route.abort()
    })
    await context.addInitScript(port => {
        localStorage.setItem('language', JSON.stringify('en_US'))
        localStorage.setItem('externalControllers', JSON.stringify([{ hostname: '127.0.0.1', port, secret: '' }]))
        localStorage.setItem('externalControllerIndex', '0')
        const fixture = window.throughputEdgesFixture = {
            hidden: false, phase: 'warmup', received: 0, frames: [], latest: null,
            drawCount: 0, hiddenDraws: 0, dropped: 0, maxDrawsPerFrame: 0, nextClearProbe: false, clearProbes: [],
        }
        Object.defineProperty(document, 'hidden', { configurable: true, get: () => fixture.hidden })
        Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => fixture.hidden ? 'hidden' : 'visible' })
        fixture.setHidden = hidden => { fixture.hidden = hidden; document.dispatchEvent(new Event('visibilitychange')) }
        fixture.export = () => ({
            frames: fixture.frames, latest: fixture.latest, drawCount: fixture.drawCount,
            hiddenDraws: fixture.hiddenDraws, dropped: fixture.dropped, maxDrawsPerFrame: fixture.maxDrawsPerFrame, clearProbes: fixture.clearProbes,
        })
        fixture.seedRightColumn = () => {
            const canvas = document.querySelector('canvas.connections-chart')
            const ctx = canvas.getContext('2d')
            const stripe = ctx.createImageData(1, canvas.height)
            for (let y = 0; y < canvas.height; y++) stripe.data.set([255, 0, 255, 255], y * 4)
            ctx.putImageData(stripe, canvas.width - 1, 0)
            fixture.nextClearProbe = true
        }

        const NativeWebSocket = window.WebSocket
        window.WebSocket = class extends NativeWebSocket {
            constructor(url, protocols) {
                super(url, ...(protocols === undefined ? [] : [protocols]))
                if (new URL(String(url), location.href).pathname === '/connections') {
                    this.addEventListener('message', event => { fixture.received = JSON.parse(event.data).sequence })
                }
            }
        }

        // Record operations while forwarding every call to the native Canvas.
        // No production exports, state mutation or test-only chart hook is used.
        const pathOperations = new WeakMap()
        const NativePath = window.Path2D
        window.Path2D = class extends NativePath {
            constructor(source) {
                super(...(source === undefined ? [] : [source]))
                pathOperations.set(this, (pathOperations.get(source) || []).map(operation => operation.slice()))
            }
        }
        for (const name of ['moveTo', 'lineTo', 'quadraticCurveTo', 'bezierCurveTo', 'rect', 'closePath']) {
            const native = NativePath.prototype[name]
            window.Path2D.prototype[name] = function(...args) {
                pathOperations.get(this).push([name, ...args])
                return native.apply(this, args)
            }
        }
        const proto = CanvasRenderingContext2D.prototype
        const activePaths = new WeakMap()
        const drawsAt = new Map()
        const isChart = ctx => ctx.canvas.classList.contains('connections-chart')
        const clear = proto.clearRect
        proto.clearRect = function(...args) {
            if (isChart(this)) {
                const at = performance.now()
                fixture.drawCount++
                if (fixture.hidden) fixture.hiddenDraws++
                const count = (drawsAt.get(at) || 0) + 1
                drawsAt.set(at, count)
                if (drawsAt.size > 128) drawsAt.delete(drawsAt.keys().next().value)
                fixture.maxDrawsPerFrame = Math.max(fixture.maxDrawsPerFrame, count)
                const frame = { at, phase: fixture.phase, sequence: fixture.received, width: this.canvas.clientWidth, height: this.canvas.clientHeight, strokes: [], fills: [], heads: [], clips: [] }
                fixture.latest = frame
                if (fixture.phase !== 'warmup') {
                    if (fixture.frames.length < 2000) fixture.frames.push(frame)
                    else fixture.dropped++
                }
            }
            const returned = clear.apply(this, args)
            if (isChart(this) && fixture.nextClearProbe) {
                fixture.nextClearProbe = false
                const pixels = this.getImageData(this.canvas.width - 1, 0, 1, this.canvas.height).data
                let remaining = 0
                for (let index = 3; index < pixels.length; index += 4) if (pixels[index]) remaining++
                fixture.clearProbes.push({ width: this.canvas.width, height: this.canvas.height, dpr: devicePixelRatio, remaining })
            }
            return returned
        }
        const begin = proto.beginPath
        proto.beginPath = function(...args) { activePaths.set(this, []); return begin.apply(this, args) }
        for (const name of ['rect', 'moveTo', 'lineTo', 'quadraticCurveTo']) {
            const native = proto[name]
            proto[name] = function(...args) {
                if (isChart(this)) activePaths.get(this)?.push([name, ...args])
                return native.apply(this, args)
            }
        }
        const stroke = proto.stroke
        proto.stroke = function(...args) {
            if (isChart(this) && fixture.latest && pathOperations.has(args[0])) {
                fixture.latest.strokes.push({ width: this.lineWidth, color: this.strokeStyle, opacity: this.globalAlpha, operations: pathOperations.get(args[0]).map(operation => operation.slice()) })
            }
            return stroke.apply(this, args)
        }
        const fill = proto.fill
        proto.fill = function(...args) {
            if (isChart(this) && fixture.latest && pathOperations.has(args[0])) fixture.latest.fills.push({ operations: pathOperations.get(args[0]).map(operation => operation.slice()) })
            return fill.apply(this, args)
        }
        const arc = proto.arc
        proto.arc = function(x, y, radius, ...args) {
            if (isChart(this) && fixture.latest) fixture.latest.heads.push({ x, y, radius, opacity: this.globalAlpha })
            return arc.call(this, x, y, radius, ...args)
        }
        const clip = proto.clip
        proto.clip = function(...args) {
            if (isChart(this) && fixture.latest) fixture.latest.clips.push((pathOperations.get(args[0]) || activePaths.get(this) || []).map(operation => operation.slice()))
            return clip.apply(this, args)
        }
    }, port)

    page = await context.newPage()
    page.setDefaultTimeout(15000)
    page.on('pageerror', error => errors.push(String(error)))
    // Install before navigation, then allow the application to load normally.
    // Afterwards all sample delivery, rAF and expiry checks share one clock.
    await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') })
    await page.goto(`http://127.0.0.1:${port}/#/connections`, { waitUntil: 'networkidle' })
    await page.locator('canvas.connections-chart').waitFor({ state: 'visible' })
    await received()
    await page.clock.pauseAt(new Date(await page.evaluate(() => Date.now()) + 1000))
    await page.evaluate(() => window.throughputEdgesFixture.setHidden(true))
    await until(() => sockets.size === 0, 'initial transport closes before the controlled baseline')
    anchor = await page.evaluate(() => performance.now())
    sample(0)
    await advanceTo(500)
    await page.evaluate(() => window.throughputEdgesFixture.setHidden(false))
    await received()
    await advanceTo(628)

    await phase('first-sample')
    await advanceTo(1500)
    await send(1000)
    await advanceTo(1628)
    const firstState = await page.evaluate(() => window.throughputEdgesFixture.export())
    const firstFrame = firstState.frames.find(frame => frame.sequence === sequence && frame.heads.filter(head => head.radius === 3).length === 2)
    assert.ok(firstFrame, 'the first valid rate sample draws both markers without waiting for future samples')
    assert.ok(firstFrame.at - anchor - 1500 <= 128, 'the first sample appears within one normal publication interval')
    assert.equal(firstFrame.strokes.flatMap(stroke => stroke.operations).filter(operation => operation[0] === 'quadraticCurveTo').length, 0, 'the first-sample check does not rely on a pre-existing curve')
    observations.push({ label: 'first-sample', visibleWithinMs: firstFrame.at - anchor - 1500, headCount: 2 })
    await screenshot('first-live-sample')
    await phase('warmup')

    // A stable 500 ms delivery offset is deliberately present initially. Later
    // faster delivery improves the source clock mapping without changing rates.
    for (let tick = 2; tick <= 64; tick++) {
        await advanceTo(tick * 1000 + 500)
        await send(tick * 1000)
        await advanceTo(tick * 1000 + 628)
    }
    await phase('steady')
    let maxLatestLatency = 0
    screenshots = Array.from({ length: 12 }, (_, index) => 67200 + index * 100)
    for (let tick = 65; tick <= 70; tick++) {
        await advanceTo(tick * 1000 + 500)
        await send(tick * 1000)
        await advanceTo(tick * 1000 + 628)
        maxLatestLatency = Math.max(maxLatestLatency, await assertNewest(tick, tick * 1000 + 500))
    }
    await screenshot('steady-complete-window')
    let state = await page.evaluate(() => window.throughputEdgesFixture.export())
    observations.push(metrics(state.frames, 'steady', 0.2))

    await phase('delivery-jitter')
    const delays = [500, 250, 0, 400, 100, 500, 0, 350]
    for (let index = 0; index < delays.length; index++) {
        const tick = 71 + index
        await advanceTo(tick * 1000 + delays[index])
        await send(tick * 1000)
        await advanceTo(tick * 1000 + delays[index] + 128)
        maxLatestLatency = Math.max(maxLatestLatency, await assertNewest(tick, tick * 1000 + delays[index]))
    }
    await screenshot('delivery-jitter')
    state = await page.evaluate(() => window.throughputEdgesFixture.export())
    observations.push(metrics(state.frames, 'delivery-jitter', 0.7))
    const rateText = await page.locator('.connections-rate.is-down .connections-rate-value').textContent()
    const expectedRate = rates[78 % rates.length] * MiB
    assert.ok(Math.abs(bytesPerSecond(rateText) - expectedRate) <= expectedRate * 0.011, 'top-level rate still reports the newest source sample without the chart delay')
    observations.push({ label: 'latest-number-and-chart', text: rateText, expectedBytesPerSecond: expectedRate, deliveryDelaysMs: delays, maxLatestChartLatencyMs: maxLatestLatency })

    await phase('tail-expiry')
    await advanceTo(79750)
    const fading = await page.evaluate(() => window.throughputEdgesFixture.latest.heads.filter(head => head.radius === 3))
    assert.equal(fading.length, 2, 'a briefly delayed sample retains the short latest-value marker')
    for (const head of fading) assert.ok(head.opacity > 0 && head.opacity < 0.8, 'the marker fades between 1.5 and 2 seconds of source age')
    await advanceTo(80080)
    assert.equal(await page.evaluate(() => window.throughputEdgesFixture.latest.heads.filter(head => head.radius === 3).length), 0, 'the held-value marker expires after two seconds without inventing observations')
    observations.push({ label: 'held-tail-expiry', fadedOpacity: fading[0].opacity, expiredWithinMs: 2080 })

    await phase('gap')
    await advanceTo(90000)
    state = await page.evaluate(() => window.throughputEdgesFixture.export())
    assert.equal(state.latest.heads.filter(head => Math.abs(head.radius - 3) < 1e-6).length, 0, 'a stale stream has no current head')
    const staleRate = await page.locator('.connections-rate.is-down .connections-rate-value').textContent()
    assert.equal(bytesPerSecond(staleRate), 0, 'an expired measurement does not retain a nonzero current rate')
    await send(90000)
    for (let tick = 91; tick <= 95; tick++) {
        await advanceTo(tick * 1000)
        await send(tick * 1000)
        await advanceTo(tick * 1000 + 128)
    }
    await screenshot('source-gap')
    state = await page.evaluate(() => window.throughputEdgesFixture.export())
    const gapGeometry = segments(state.latest)
    const sections = new Map()
    for (const piece of gapGeometry) {
        const range = sections.get(piece.section) || [Infinity, -Infinity]
        range[0] = Math.min(range[0], piece.start[0]); range[1] = Math.max(range[1], piece.end[0])
        sections.set(piece.section, range)
    }
    const ranges = [...sections.values()].sort((a, b) => a[0] - b[0])
    const largestGap = Math.max(0, ...ranges.slice(1).map((range, index) => range[0] - ranges[index][1]))
    assert.ok(largestGap >= state.latest.width * 8 / 60, 'the missing sampling interval is a real unconnected gap in the Canvas path')
    observations.push({ label: 'source-gap', sections: ranges.length, gapPixels: largestGap, staleRate })

    await phase('hidden')
    await page.evaluate(() => window.throughputEdgesFixture.setHidden(true))
    await until(() => sockets.size === 0, 'hiding the chart suspends the real statistics transport')
    const beforeHidden = await page.evaluate(() => window.throughputEdgesFixture.drawCount)
    const cdp = await context.newCDPSession(page)
    await cdp.send('Page.setWebLifecycleState', { state: 'frozen' })
    sample(125000)
    await cdp.send('Page.setWebLifecycleState', { state: 'active' })
    await page.clock.fastForward(30000)
    elapsed += 30000
    assert.equal(await page.evaluate(() => window.throughputEdgesFixture.drawCount), beforeHidden, 'a hidden and resumed-from-freeze chart draws no background frames')
    await phase('resume')
    const openedBefore = opened
    await page.evaluate(() => window.throughputEdgesFixture.setHidden(false))
    await received()
    await until(() => opened === openedBefore + 1 && sockets.size === 1, 'resume uses one new socket')
    await advanceTo(elapsed + 128)
    const baselineRate = await page.locator('.connections-rate.is-down .connections-rate-value').textContent()
    assert.equal(bytesPerSecond(baselineRate), 0, 'the first resumed snapshot establishes a baseline')
    for (let tick = 126; tick <= 130; tick++) {
        await advanceTo(tick * 1000 + 128)
        await send(tick * 1000)
        await advanceTo(tick * 1000 + 256)
    }
    await screenshot('resume-current-window')
    state = await page.evaluate(() => window.throughputEdgesFixture.export())
    assert.equal(state.hiddenDraws, 0, 'hidden drawing remains stopped throughout the regression')
    assert.equal(state.maxDrawsPerFrame, 1, 'resume never introduces a second drawing loop')
    assert.equal(state.dropped, 0, 'geometry observations are complete and bounded')
    const resumed = state.frames.filter(frame => frame.phase === 'resume')
    assert.ok(resumed.length > 20 && resumed.length <= 170, 'resume draws bounded current frames instead of replaying thirty seconds of animations')
    observations.push({ label: 'resume', baselineRate, frames: resumed.length, hiddenDraws: state.hiddenDraws, maxDrawsPerFrame: state.maxDrawsPerFrame })

    // Only two targeted raster cases: a short graph at 125% scaling and a
    // regular graph at 150%. This does not claim to reproduce a user's GPU.
    const rasterCases = [
        { label: 'compact-dpr125', width: 1440, height: 700, dpr: 1.25 },
        { label: 'regular-dpr150', width: 1444, height: 900, dpr: 1.5 },
    ]
    for (let index = 0; index < rasterCases.length; index++) {
        const scenario = rasterCases[index]
        await phase(scenario.label)
        await page.setViewportSize({ width: scenario.width, height: scenario.height })
        await cdp.send('Emulation.setDeviceMetricsOverride', { width: scenario.width, height: scenario.height, deviceScaleFactor: scenario.dpr, mobile: false })
        const tick = 131 + index
        await advanceTo(tick * 1000 + 128)
        await send(tick * 1000)
        await advanceTo(tick * 1000 + 256)
        const reading = await inspectPixels(scenario.label)
        if (index === 0) assert.ok(reading.height < 120, 'the compact case exercises the existing short-chart branch')
        assert.equal(reading.dpr, scenario.dpr)
        assertFillClosure(await page.evaluate(() => window.throughputEdgesFixture.latest), scenario.label)
        // Seed one physical edge column, then inspect the next normal clear.
        // This asserts pixel removal, independently of how clearRect is called.
        await page.evaluate(() => window.throughputEdgesFixture.seedRightColumn())
        await advanceTo(tick * 1000 + 320)
        const clearProbe = await page.evaluate(() => window.throughputEdgesFixture.clearProbes.at(-1))
        assert.ok(clearProbe, 'the next actual chart draw performed the edge-clear probe')
        assert.equal(clearProbe.remaining, 0, `${scenario.label}: the normal clear removes every seeded physical edge pixel`)
        await screenshot(scenario.label)
        observations.push({ ...reading, clearedEdgePixels: clearProbe.height, remainingAfterClear: clearProbe.remaining })
    }
    assert.deepEqual(errors, [])
    await persist()
    console.log(JSON.stringify({ passed: true, browser: browser.version(), controlledClock: true, controlledVisibility: true, frozenPage: true, observations, errors }, null, 2))
}

main().catch(async error => {
    console.error(error.stack)
    try { if (page && !page.isClosed()) { await screenshot('failure'); await persist() } } catch (diagnosticError) { console.error('Unable to save browser diagnostics: ' + diagnosticError) }
    console.error(JSON.stringify({ observations, errors, elapsed, sequence, opened, activeSockets: sockets.size }, null, 2))
    process.exitCode = 1
}).finally(async () => {
    if (browser) await browser.close()
    for (const socket of wss.clients) socket.terminate()
    wss.close(); server.close()
})
