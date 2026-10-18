#!/usr/bin/env node
// Isolated browser regression for connection suspension and batched presentation.
// Visibility is controlled explicitly: this tests the application's lifecycle,
// not whether a particular OS/headless browser queues background socket events.
const assert = require('node:assert/strict')
const fs = require('node:fs')
const http = require('node:http')
const path = require('node:path')
const { WebSocketServer } = require('ws')
const { chromium } = require('playwright')

const dist = path.resolve(process.env.UI_DIST_DIR || path.join(__dirname, '../dist'))
const config = { port: 0, 'socks-port': 0, 'mixed-port': 0, 'redir-port': 0, mode: 'rule', 'log-level': 'info', 'allow-lan': false }
const MiB = 1024 * 1024
let sequence = 0
function sampled(snapshot) {
    sequence++
    return { ...snapshot, sampledAt: sequence * 1000, sequence }
}
function bytesPerSecond(text) {
    const reading = text.match(/([\d.]+)\s*(B|KiB|MiB|GiB|TiB)\/s/)
    assert.ok(reading, 'a visible rate includes a number and byte unit: ' + text)
    return Number(reading[1]) * 1024 ** ['B', 'KiB', 'MiB', 'GiB', 'TiB'].indexOf(reading[2])
}
function connection(index, sourceIP = '192.0.2.' + (101 + index % 2)) {
    return {
        id: 'connection-' + index,
        metadata: { network: 'tcp', type: 'Socks5', host: 'row-' + String(index).padStart(5, '0') + '.test', sourceIP, sourcePort: String(20000 + index), destinationIP: '198.51.100.1', destinationPort: '443' },
        upload: 100, download: 200, start: new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString(),
        chains: ['DIRECT'], rule: 'Match', rulePayload: '',
    }
}

let current = sampled({ connections: Array.from({ length: 6100 }, (_, index) => connection(index)), uploadTotal: 10000, downloadTotal: 20000 })
const sockets = new Set()
const opened = []
const observations = []
const pageErrors = []
let browser
const server = http.createServer((req, res) => {
    const pathname = new URL(req.url, 'http://127.0.0.1').pathname
    const api = {
        '/version': { version: 'isolated-connection-resume-test', premium: false }, '/configs': config,
        '/connections': current, '/rules': { rules: [] },
        '/providers/proxies': { providers: {} }, '/providers/rules': { providers: {} },
        '/proxies': { proxies: { GLOBAL: { name: 'GLOBAL', type: 'Selector', now: 'DIRECT', history: [], all: ['DIRECT'] }, DIRECT: { name: 'DIRECT', type: 'Direct', history: [] } } },
    }
    if (api[pathname]) {
        res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' })
        res.end(JSON.stringify(api[pathname])); return
    }
    const file = path.join(dist, pathname === '/' ? 'index.html' : pathname)
    if (!file.startsWith(dist + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return }
    const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml', '.png': 'image/png', '.webmanifest': 'application/manifest+json' }
    res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream' })
    fs.createReadStream(file).pipe(res)
})
const wss = new WebSocketServer({ server })
wss.on('connection', (socket, req) => {
    if (new URL(req.url, 'http://127.0.0.1').pathname !== '/connections') { socket.close(); return }
    opened.push(socket)
    sockets.add(socket)
    socket.send(JSON.stringify(current))
    socket.on('close', () => sockets.delete(socket))
})

function send(snapshot) {
    current = sampled(snapshot)
    for (const socket of sockets) socket.send(JSON.stringify(current))
}
async function until(predicate, label) {
    const deadline = Date.now() + 10000
    while (!predicate() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10))
    assert.ok(predicate(), label)
}

async function main() {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
    const port = server.address().port
    browser = await chromium.launch({
        headless: true,
        executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || undefined,
        args: process.env.PLAYWRIGHT_CHROMIUM_ARGS ? JSON.parse(process.env.PLAYWRIGHT_CHROMIUM_ARGS) : [],
    })
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'en-US', serviceWorkers: 'block' })
    await context.route('**/*', route => {
        const url = new URL(route.request().url())
        return url.hostname === '127.0.0.1' && url.port === String(port) ? route.continue() : route.abort()
    })
    await context.addInitScript(port => {
        localStorage.setItem('language', JSON.stringify('en_US'))
        localStorage.setItem('externalControllers', JSON.stringify([{ hostname: '127.0.0.1', port, secret: '' }]))
        localStorage.setItem('externalControllerIndex', '0')

        const fixture = window.connectionResumeFixture = {
            hidden: false, sockets: [], chartDraws: 0, hiddenDraws: 0, maxDrawsPerFrame: 0,
            detailMutations: 0, counts: [],
        }
        Object.defineProperty(document, 'hidden', { configurable: true, get: () => fixture.hidden })
        Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => fixture.hidden ? 'hidden' : 'visible' })
        fixture.setHidden = hidden => {
            fixture.hidden = hidden
            document.dispatchEvent(new Event('visibilitychange'))
        }
        // Use a native socket and real local transport. Explicit dispatch on an
        // old socket models a callback that was already queued when it closed.
        const NativeWebSocket = window.WebSocket
        window.WebSocket = class extends NativeWebSocket {
            constructor(url, protocols) {
                super(url, ...(protocols === undefined ? [] : [protocols]))
                if (new URL(String(url), location.href).pathname === '/connections') fixture.sockets.push(this)
            }
        }
        fixture.deliver = (index, snapshot) => fixture.sockets[index].dispatchEvent(new MessageEvent('message', { data: JSON.stringify(snapshot) }))

        // Observe actual drawing; leave requestAnimationFrame's scheduling and
        // cancellation intact. Multiple chart loops must not draw one frame twice.
        const nativeFrame = window.requestAnimationFrame.bind(window)
        let frameTime = null
        const drawsByFrame = new Map()
        window.requestAnimationFrame = callback => nativeFrame(now => {
            const previous = frameTime
            frameTime = now
            try { callback(now) } finally { frameTime = previous }
        })
        fixture.frames = count => new Promise(resolve => {
            const step = () => { if (--count <= 0) resolve(); else nativeFrame(step) }
            nativeFrame(step)
        })
        const clearRect = CanvasRenderingContext2D.prototype.clearRect
        CanvasRenderingContext2D.prototype.clearRect = function(...args) {
            if (this.canvas.classList.contains('connections-chart')) {
                fixture.chartDraws++
                if (fixture.hidden) fixture.hiddenDraws++
                if (frameTime !== null) {
                    const count = (drawsByFrame.get(frameTime) || 0) + 1
                    drawsByFrame.set(frameTime, count)
                    fixture.maxDrawsPerFrame = Math.max(fixture.maxDrawsPerFrame, count)
                    if (drawsByFrame.size > 128) drawsByFrame.delete(drawsByFrame.keys().next().value)
                }
            }
            return clearRect.apply(this, args)
        }
        fixture.watchHiddenDetail = () => {
            fixture.detailMutations = 0
            const detail = document.querySelector('.connection-info')
            if (detail) {
                const observer = new MutationObserver(records => { fixture.detailMutations += records.length })
                observer.observe(detail, { childList: true, characterData: true, subtree: true })
                fixture.stopDetailWatch = () => observer.disconnect()
            }
        }
        fixture.watchCounts = () => {
            const read = () => {
                const count = Number(document.querySelector('.connections-source-filters > button .device-count').textContent.replace(/[()\s]/g, ''))
                if (fixture.counts.at(-1) !== count) fixture.counts.push(count)
            }
            fixture.counts = []
            read()
            const observer = new MutationObserver(read)
            observer.observe(document.querySelector('.connections-source-filters'), { childList: true, characterData: true, subtree: true })
            fixture.stopCountWatch = () => observer.disconnect()
        }
    }, port)
    const page = await context.newPage()
    page.setDefaultTimeout(15000)
    page.on('pageerror', error => pageErrors.push(String(error)))
    await page.goto(`http://127.0.0.1:${port}/#/connections`, { waitUntil: 'networkidle' })
    const waitCount = count => page.getByRole('button', { name: `All (${count})`, exact: true }).waitFor()
    const waitRows = count => page.waitForFunction(count => Number(document.querySelector('[role="table"]')?.getAttribute('aria-rowcount')) === count + 1, count)
    const details = page.locator('.connections-drawer')
    async function counts() {
        return page.evaluate(() => [...document.querySelectorAll('.connections-source-filters > button:not([aria-hidden="true"])')].map(button => ({
            label: button.querySelector('.device-label').textContent,
            count: Number(button.querySelector('.device-count').textContent.replace(/[()\s]/g, '')),
            selected: button.getAttribute('aria-pressed'),
        })))
    }
    async function position() {
        return page.evaluate(() => {
            const scroll = document.querySelector('.connections-scroll')
            const top = document.querySelector('.connections-header').getBoundingClientRect().bottom
            const first = [...document.querySelectorAll('.connections-row')].find(row => row.getBoundingClientRect().bottom > top)
            return { top: scroll.scrollTop, left: scroll.scrollLeft, first: first?.dataset.connectionId, rendered: document.querySelectorAll('.connections-row').length }
        })
    }
    const zeroRates = () => page.evaluate(() => {
        const readings = [...document.querySelectorAll('.connections-rate-value .rolling-bytes-number')]
        return readings.length === 2 && readings.every(item => Number(item.textContent) === 0)
    })

    await waitCount(6100); await waitRows(6100)
    await page.getByText('Keep closed connections', { exact: true }).click()
    let active = [connection(7000), connection(7001), connection(7002)]
    send({ ...current, connections: active })
    await waitCount(5003)
    await page.getByText(/Oldest records removed: 1100/).waitFor()
    assert.equal(await page.getByRole('checkbox', { name: 'Keep closed connections', exact: true }).isChecked(), true)

    // Establish a measured interval, then change totals before suspending. The
    // resumed first sample must still be a baseline even when the pause is short.
    const measuredAt = await page.evaluate(() => performance.now())
    await page.waitForFunction(at => performance.now() - at >= 1100, measuredAt)
    active = active.map(item => ({ ...item, download: item.download + MiB }))
    send({ connections: active, uploadTotal: 20000, downloadTotal: 20000 + 3 * MiB })
    await page.waitForFunction(() => Number(document.querySelector('.connections-rate.is-down .rolling-bytes-number')?.textContent) > 0)
    await page.locator('[data-connection-id="connection-7002"]').click()
    await details.getByText('connection-7002', { exact: true }).waitFor()
    await page.getByRole('button', { name: /^192\.0\.2\.101 \(/ }).click()
    await waitRows(2502)
    await page.locator('.connections-scroll').evaluate(element => { element.scrollTop = 4500; element.scrollLeft = 320 })
    await page.evaluate(() => window.connectionResumeFixture.frames(3))
    const before = { counts: await counts(), position: await position() }
    assert.ok(before.position.top > 0 && before.position.rendered < 70)

    const oldSocket = await page.evaluate(() => window.connectionResumeFixture.sockets.length - 1)
    const beforeOpened = opened.length
    await page.evaluate(() => window.connectionResumeFixture.setHidden(true))
    await until(() => sockets.size === 0, 'hidden connections page closes its statistics socket')
    const hiddenDraws = await page.evaluate(() => window.connectionResumeFixture.chartDraws)
    await page.evaluate(() => window.connectionResumeFixture.frames(4))
    assert.equal(await page.evaluate(() => window.connectionResumeFixture.chartDraws), hiddenDraws, 'hidden chart stops drawing')

    // Freeze only this isolated page after the application has suspended. The
    // synthetic visibility state above is not presented as an OS tab-switch test.
    const cdp = await context.newCDPSession(page)
    await cdp.send('Page.setWebLifecycleState', { state: 'frozen' })
    active = active.map(item => ({ ...item, download: item.download + 256 * MiB }))
    current = sampled({ connections: active, uploadTotal: 1024 * MiB, downloadTotal: 2048 * MiB })
    await cdp.send('Page.setWebLifecycleState', { state: 'active' })
    await page.evaluate(() => window.connectionResumeFixture.setHidden(false))
    await until(() => opened.length === beforeOpened + 1 && sockets.size === 1, 'resume opens one fresh statistics socket')
    await page.waitForFunction(() => document.querySelector('.connections-traffic-item .reading')?.textContent === '2.00 GiB')
    await waitCount(5003); await waitRows(2502)
    await details.getByText('connection-7002', { exact: true }).waitFor()
    assert.deepEqual(await counts(), before.counts, 'history and source selection survive reader suspension')
    assert.equal(await page.getByRole('checkbox', { name: 'Keep closed connections', exact: true }).isChecked(), true)
    assert.equal(await zeroRates(), true, 'first resumed totals do not report missed bytes as a current rate')
    await page.waitForFunction(draws => window.connectionResumeFixture.chartDraws > draws, hiddenDraws)
    const after = await position()
    assert.equal(after.first, before.position.first, 'resume retains the connection reading anchor')
    assert.ok(Math.abs(after.top - before.position.top) <= 2 && after.left === before.position.left, 'resume preserves both scroll offsets')
    observations.push({ label: 'resume', retained: 5000, active: 3, selectedID: 'connection-7002', before: before.position, after })

    // A valid newer sequence ensures the stale-socket check is doing the work;
    // rejecting an old sequence in the sample clock would hide that regression.
    const poison = sampled({ connections: [connection(99999, '198.18.0.99')], uploadTotal: 1, downloadTotal: 1 })
    await page.evaluate(({ index, snapshot }) => window.connectionResumeFixture.deliver(index, snapshot), { index: oldSocket, snapshot: poison })
    active = active.map(item => item.id === 'connection-7002' ? { ...item, metadata: { ...item.metadata, host: 'resume-confirmed.test' } } : item)
    send({ ...current, connections: active })
    await details.getByText('resume-confirmed.test:443', { exact: true }).waitFor()
    assert.equal(await page.locator('[data-source-key="device:198.18.0.99"]').count(), 0, 'an old socket cannot add live or retained poison records')
    assert.deepEqual(await counts(), before.counts)

    // Repeated visibility notifications and resume cycles must not add sockets
    // or independent chart drawing loops.
    for (let cycle = 0; cycle < 2; cycle++) {
        const openedBeforeCycle = opened.length
        await page.evaluate(() => { window.connectionResumeFixture.setHidden(true); window.connectionResumeFixture.setHidden(true) })
        await until(() => sockets.size === 0, 'repeated hidden notifications keep the statistics transport stopped')
        if (cycle === 0) {
            const { sampledAt, sequence, ...legacy } = current
            current = legacy
        } else current = sampled(current)
        await page.evaluate(() => { window.connectionResumeFixture.setHidden(false); window.connectionResumeFixture.setHidden(false) })
        await until(() => opened.length === openedBeforeCycle + 1 && sockets.size === 1, 'repeated visible notifications create one socket')
        await page.evaluate(() => window.connectionResumeFixture.frames(8))
        assert.deepEqual(await counts(), before.counts, 'resuming both modern and legacy controllers preserves retained records')
    }
    assert.equal(await page.evaluate(() => window.connectionResumeFixture.hiddenDraws), 0)
    assert.equal(await page.evaluate(() => window.connectionResumeFixture.maxDrawsPerFrame), 1, 'chart draws at most once in each animation frame after resuming')

    await details.locator('.connections-drawer-close').click()
    await page.waitForFunction(() => document.querySelector('.drawer-inner')?.getAttribute('aria-hidden') === 'true')
    await page.evaluate(() => { window.connectionResumeFixture.watchHiddenDetail(); document.querySelector('.connections-scroll').scrollTop = 0 })
    active = active.map(item => item.id === 'connection-7002' ? { ...item, download: item.download + MiB, metadata: { ...item.metadata, host: 'hidden-detail-update.test' } } : item)
    send({ ...current, connections: active })
    await page.locator('[data-connection-id="connection-7002"]').getByText('hidden-detail-update.test:443', { exact: true }).waitFor()
    assert.equal(await page.evaluate(() => window.connectionResumeFixture.detailMutations), 0, 'new connection data does not rewrite hidden detail content')
    await page.evaluate(() => window.connectionResumeFixture.stopDetailWatch?.())
    await page.locator('[data-connection-id="connection-7002"]').click()
    await details.getByText('hidden-detail-update.test:443', { exact: true }).waitFor()
    await details.locator('.connections-drawer-close').click()

    await page.getByRole('button', { name: /^All \(/ }).click()
    await page.getByText('Clear closed records', { exact: true }).click()
    await waitCount(3); await waitRows(3)
    const burstActive = tick => active.map(item => item.id === 'connection-7002' ? { ...item, download: item.download + tick * MiB } : item)
    // Samples represent one second and one MiB each, regardless of how quickly
    // the browser dispatches their message callbacks below.
    const burst = Array.from({ length: 30 }, (_, index) => sampled({
        ...current,
        downloadTotal: current.downloadTotal + (index + 1) * MiB,
        connections: [...burstActive(index + 1), { ...connection(8000 + index, '203.0.113.77'), upload: 0, download: 0 }],
    }))
    burst.push(sampled({ ...current, downloadTotal: current.downloadTotal + 31 * MiB, connections: burstActive(31) }))
    await page.evaluate(snapshots => {
        const fixture = window.connectionResumeFixture
        fixture.watchCounts()
        const index = fixture.sockets.length - 1
        for (const snapshot of snapshots) fixture.deliver(index, snapshot)
    }, burst)
    await waitCount(33)
    await page.getByRole('button', { name: '203.0.113.77 (30)', exact: true }).waitFor()
    const displayedCounts = await page.evaluate(() => { window.connectionResumeFixture.stopCountWatch(); return window.connectionResumeFixture.counts })
    assert.equal(displayedCounts.at(-1), 33)
    assert.ok(displayedCounts.length < burst.length, 'a burst reaches its final state without presenting every intermediate count')
    const totalRate = await page.locator('.connections-rate.is-down .connections-rate-value').textContent()
    // Account for the display's decimal rounding around the KiB/MiB boundary,
    // without allowing callback timing to inflate the actual measurement.
    assert.ok(Math.abs(bytesPerSecond(totalRate) - MiB) <= MiB * 0.011, 'source sampling time keeps burst throughput at one MiB per second: ' + totalRate)
    await page.getByRole('button', { name: '192.0.2.101 (2)', exact: true }).click()
    await waitRows(2)
    const connectionRate = await page.locator('[data-connection-id="connection-7002"] .connections-speed').textContent()
    assert.ok(Math.abs(bytesPerSecond(connectionRate) - MiB) <= MiB * 0.011, 'the selected connection also uses the source sampling interval: ' + connectionRate)
    await page.getByRole('button', { name: '203.0.113.77 (30)', exact: true }).click()
    await waitRows(30)
    assert.ok(await page.locator('.connections-row').count() > 0)
    assert.equal(await page.locator('.connections-row:not(.completed)').count(), 0, 'all short-lived burst connections are retained as closed')
    observations.push({ label: 'burst', snapshots: burst.length, retainedIntermediateConnections: 30, displayedCounts, totalRate, connectionRate })
    assert.deepEqual(pageErrors, [])
    console.log(JSON.stringify({ passed: true, browser: browser.version(), controlledVisibility: true, frozenPage: true, observations, pageErrors }, null, 2))
}

main().catch(error => {
    console.error(error.stack)
    console.error(JSON.stringify({ observations, pageErrors, opened: opened.length, activeSockets: sockets.size }, null, 2))
    process.exitCode = 1
}).finally(async () => {
    if (browser) await browser.close()
    for (const socket of wss.clients) socket.terminate()
    wss.close(); server.close()
})
