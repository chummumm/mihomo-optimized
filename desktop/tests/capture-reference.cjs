#!/usr/bin/env node
'use strict'

// Render the unmodified, uploaded desktop frontend against the observations
// captured by the native acceptance test. This is a visual reference, not a
// replacement for the native lifecycle/security checks.
const assert = require('node:assert/strict')
const crypto = require('node:crypto')
const fs = require('node:fs')
const http = require('node:http')
const path = require('node:path')
const { createRequire } = require('node:module')

const root = process.cwd()
const source = path.resolve(process.env.REFERENCE_SOURCE || 'artifacts/reference-source')
const fixtureFile = path.resolve(process.env.REFERENCE_FIXTURE || 'artifacts/verification/visual-fixture.json')
const output = path.resolve(process.env.REFERENCE_OUTPUT || 'artifacts/verification/reference')
const partial = process.env.REFERENCE_ALLOW_PARTIAL === '1'
const dist = path.join(source, 'dist-desktop')
const targets = [0, 16, 40, 83, 125, 167, 220, 250, 300, 333, 460, 700, 900, 1400, 1800, 2800]
const requiredScenarios = ['home', 'proxies', 'settings-dark-en', 'search', 'rules', 'profiles',
    'config-editor', 'config-unsaved', 'import-config', 'tun-consent', 'dns', 'dns-queries', 'logs', 'connections', 'home-narrow', 'tray']
const requiredMotions = ['home-to-proxies', 'settings-language', 'search-open', 'settings-switch',
    'editor-open', 'narrow-navigation']
const report = {
    schema: 1, kind: 'unmodified-frontend-reference', source: path.relative(root, source),
    fixtureSHA256: '', browser: null, scenarios: [], motions: [], errors: [], partial,
    comparison: 'Captured evidence only; no automatic assertion of pixel or motion parity.',
}
let browser
let server
let websocketServer
let active
const sockets = new Set()
const secret = crypto.randomBytes(32).toString('hex')
const secretless = value => String(value).split(secret).join('[fixture-secret]')
const clone = value => structuredClone(value)
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))
const sha256 = buffer => crypto.createHash('sha256').update(buffer).digest('hex')
const finite = value => typeof value === 'number' && Number.isFinite(value)

function checkpoint () {
    fs.mkdirSync(output, { recursive: true })
    fs.writeFileSync(path.join(output, 'reference-report.json'), JSON.stringify(report, null, 2) + '\n')
}

function validateScenario (scenario) {
    assert.match(scenario.name, /^[a-z][a-z0-9-]{0,63}$/)
    assert.ok(Number.isInteger(scenario.width) && scenario.width >= 240 && scenario.width <= 4096, 'Invalid reference width')
    assert.ok(Number.isInteger(scenario.height) && scenario.height >= 200 && scenario.height <= 2160, 'Invalid reference height')
    assert.ok(finite(scenario.dpi) && scenario.dpi >= 72 && scenario.dpi <= 384, 'Invalid reference DPI')
    assert.ok(['light', 'dark', 'system'].includes(scenario.theme), 'Invalid theme preference')
    assert.ok(['zh-CN', 'en-US', 'system'].includes(scenario.language), 'Invalid language preference')
    assert.ok(scenario.status && scenario.api && scenario.config, 'Missing observed state')
    assert.ok(typeof scenario.config.content === 'string' && Buffer.byteLength(scenario.config.content) <= 4194304, 'Invalid observed configuration')
    assert.ok(scenario.status.controller === undefined, 'Do not export a native controller credential')
    assert.ok(typeof scenario.capturedAt === 'string' && Number.isFinite(Date.parse(scenario.capturedAt)), 'Missing capture clock')
    for (const endpoint of ['/configs', '/proxies', '/rules', '/providers/proxies', '/providers/rules', '/connections', '/version']) {
        assert.ok(Object.hasOwn(scenario.api, endpoint), 'Missing observed endpoint: ' + endpoint)
    }
}

function browserPath (chromium) {
    const requested = process.env.REFERENCE_BROWSER_EXECUTABLE || process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH
    if (requested) {
        assert.ok(fs.existsSync(requested), 'The requested reference browser is unavailable')
        return requested
    }
    const candidates = process.platform === 'win32'
        ? ['ProgramFiles', 'ProgramFiles(x86)', 'LOCALAPPDATA'].flatMap(key => process.env[key]
            ? [path.join(process.env[key], 'Google/Chrome/Application/chrome.exe'), path.join(process.env[key], 'Microsoft/Edge/Application/msedge.exe')]
            : [])
        : ['/tmp/mihomo-reference-chrome/opt/google/chrome/chrome', '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser']
    candidates.push(chromium.executablePath())
    const executable = candidates.find(candidate => fs.existsSync(candidate))
    assert.ok(executable, 'Install Chrome or Edge, or set REFERENCE_BROWSER_EXECUTABLE')
    return executable
}

function sendJSON (res, value, code = 200) {
    res.writeHead(code, { 'content-type': 'application/json', 'cache-control': 'no-store' })
    res.end(JSON.stringify(value))
}

function readQueryFixture (state, url) {
    const data = clone(state.api[url.pathname])
    const requested = Number(url.searchParams.get('limit') || 50)
    const captured = state.scenario.name === 'dns-queries' ? 100 : 50
    if (requested === captured) return data
    // The 100-row capture is reached through the real default 50-row UI. The
    // transitional response uses only rows actually observed in that capture.
    // No other-page contents or cursors are synthesized.
    if (requested === 50 && captured === 100 && Array.isArray(data.items)) {
        data.items = data.items.slice(0, 50)
        if (data.total <= 50) data.page_cursors = ['']
        else {
            delete data.page_cursors
            delete data.anchor_id
            delete data.total
        }
        data.has_more = data.items.length < (state.api[url.pathname].total ?? state.api[url.pathname].items.length)
        data.next_cursor = data.has_more ? data.items.at(-1)?.id || '' : ''
        return data
    }
    throw new Error('No observed DNS response for the requested page size')
}

function connectionFrames (state) {
    const snapshot = state.api['/connections']
    assert.ok(finite(snapshot.uploadTotal) && snapshot.uploadTotal >= 0 &&
        finite(snapshot.downloadTotal) && snapshot.downloadTotal >= 0, 'Invalid observed connection totals')
    const observed = state.scenario.streams?.traffic
    const samples = (observed?.samples || []).map(sample => {
        assert.ok(finite(sample.ageMilliseconds) && sample.ageMilliseconds >= 0 &&
            finite(sample.up) && sample.up >= 0 && finite(sample.down) && sample.down >= 0, 'Invalid observed chart sample')
        return sample
    }).sort((a, b) => b.ageMilliseconds - a.ageMilliseconds)
    if (!samples.length) {
        state.caveats.push('No native traffic chart samples were present on this surface; the connection snapshot is used as an unmeasured baseline.')
        return [{ ...clone(snapshot), sequence: 1, sampledAt: 0 }]
    }
    const lastAge = samples.at(-1).ageMilliseconds
    const firstAt = samples[0].ageMilliseconds + (samples[0].spanMilliseconds || 1000)
    let previousAge = firstAt
    let previousSegment = samples[0].segment
    const intervals = samples.map(sample => {
        const elapsed = previousAge - sample.ageMilliseconds
        const reset = sample.segment !== undefined && previousSegment !== undefined && sample.segment !== previousSegment
        previousAge = sample.ageMilliseconds
        previousSegment = sample.segment
        return { sample, elapsed, reset }
    })
    const increment = intervals.reduce((sum, { sample, elapsed, reset }) => ({
        up: sum.up + (reset || elapsed > 10000 ? 0 : sample.up * elapsed / 1000),
        down: sum.down + (reset || elapsed > 10000 ? 0 : sample.down * elapsed / 1000),
    }), { up: 0, down: 0 })
    // A rate history and a later cumulative snapshot can straddle a native
    // restart. In that case there is no truthful reconstruction of old counters.
    const tolerance = 1
    if (increment.up > snapshot.uploadTotal + tolerance || increment.down > snapshot.downloadTotal + tolerance) {
        state.caveats.push('Observed traffic rates exceed the captured cumulative counters; historical rate reconstruction was omitted rather than inventing counters.')
        return [{ ...clone(snapshot), sequence: 1, sampledAt: 0 }]
    }
    let upload = Math.max(0, snapshot.uploadTotal - increment.up)
    let download = Math.max(0, snapshot.downloadTotal - increment.down)
    let sequence = 0
    const frames = [{ ...clone(snapshot), uploadTotal: upload, downloadTotal: download, sequence: ++sequence, sampledAt: 0 }]
    for (const { sample, elapsed, reset } of intervals) {
        if (elapsed <= 0) continue
        if (!reset && elapsed <= 10000) {
            upload += sample.up * elapsed / 1000
            download += sample.down * elapsed / 1000
        }
        frames.push({ ...clone(snapshot), uploadTotal: upload, downloadTotal: download, sequence: ++sequence,
            sampledAt: firstAt - sample.ageMilliseconds })
    }
    frames.at(-1).uploadTotal = snapshot.uploadTotal
    frames.at(-1).downloadTotal = snapshot.downloadTotal
    state.traffic = { observedSamples: samples.length, replayedFrames: frames.length, latestObservedAgeMilliseconds: lastAge,
        delivery: 'Ordered local WebSocket replay; the original controller maps its source clock to browser arrival time.' }
    state.caveats.push('Traffic samples use observed rates and totals, but browser delivery/settling time can shift their horizontal position; inspect actual capture timing.')
    return frames
}

async function createServer (WebSocketServer) {
    websocketServer = new WebSocketServer({ noServer: true })
    server = http.createServer((req, res) => {
        try {
            const url = new URL(req.url, 'http://127.0.0.1')
            const state = active
            if (!state) return sendJSON(res, { error: 'No active reference scenario' }, 503)
            if (Object.hasOwn(state.api, url.pathname)) {
                state.requests.push({ method: req.method, path: url.pathname })
                assert.equal(req.headers.authorization, 'Bearer ' + secret, 'Reference API credential mismatch')
                assert.equal(req.method, 'GET', 'Unexpected mutation of captured API')
                const response = url.pathname === '/dns/observability/queries' ? readQueryFixture(state, url) : state.api[url.pathname]
                return sendJSON(res, response)
            }
            const relative = decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname)
            const file = path.resolve(dist, '.' + relative)
            if (!file.startsWith(dist + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
                if (url.pathname !== '/favicon.ico') state.errors.push('Unobserved local endpoint: ' + url.pathname)
                res.writeHead(404); res.end(); return
            }
            const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.png': 'image/png',
                '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.json': 'application/json' }
            res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream', 'cache-control': 'no-store' })
            fs.createReadStream(file).pipe(res)
        } catch (error) {
            active?.errors.push(secretless(error.message))
            if (!res.headersSent) sendJSON(res, { error: 'Invalid reference request' }, 500)
            else res.end()
        }
    })
    server.on('upgrade', (req, socket, head) => {
        const state = active
        const url = new URL(req.url, 'http://127.0.0.1')
        if (!state || url.searchParams.get('token') !== secret ||
            !['/connections', '/logs', '/traffic', '/memory'].includes(url.pathname)) {
            state?.errors.push('Rejected unobserved WebSocket endpoint: ' + url.pathname)
            socket.destroy()
            return
        }
        websocketServer.handleUpgrade(req, socket, head, stream => {
            sockets.add(stream)
            stream.on('close', () => sockets.delete(stream))
            stream.on('error', () => {})
            state.streams.add(url.pathname)
            const send = value => { if (stream.readyState === 1) stream.send(JSON.stringify(value)) }
            // Let the application's effect subscribe before delivering its data.
            setTimeout(() => {
                if (active !== state || stream.readyState !== 1) return
                if (url.pathname === '/connections') for (const frame of state.connectionFrames) send(frame)
                else if (url.pathname === '/logs') {
                    const records = state.scenario.streams?.logs || []
                    for (const record of records) send({ type: String(record.type).toLowerCase(), payload: record.payload,
                        __referenceTime: record.time })
                    state.logs = { observed: records.length, sourceHistoryLimit: 200 }
                    if (records.length > 200) state.caveats.push('The unmodified frontend retains 200 logs; the native fixture contains more than that original limit.')
                } else if (url.pathname === '/traffic') {
                    const traffic = state.scenario.streams?.traffic || { up: 0, down: 0 }
                    send({ up: traffic.up, down: traffic.down })
                } else send({ inuse: 0, oslimit: 0 })
            }, 50)
        })
    })
    await new Promise((resolve, reject) => {
        server.once('error', reject)
        server.listen(0, '127.0.0.1', resolve)
    })
    return 'http://127.0.0.1:' + server.address().port
}

// This adapter supplies the same native IPC and controller transport as the
// original frontend. It never imports or changes a React/Jotai application store.
function initializeFixture ({ scenario, origin, secret }) {
    // The native capture may already have navigation history. React Router
    // preserves a supplied idx when it starts, so restore the observed Back
    // availability before mounting without altering any rendered component.
    history.replaceState({ ...history.state, idx: scenario.canGoBack === true ? 1 : 0 }, '', location.href)
    const OriginalDate = Date
    const startedAt = OriginalDate.now()
    const observedAt = OriginalDate.parse(scenario.capturedAt)
    let eventTime
    class ObservedDate extends OriginalDate {
        constructor (...args) { super(...(args.length ? args : [eventTime ?? observedAt + OriginalDate.now() - startedAt])) }
        static now () { return eventTime ?? observedAt + OriginalDate.now() - startedAt }
    }
    window.Date = ObservedDate
    localStorage.clear()
    localStorage.setItem('theme', scenario.theme)
    localStorage.setItem('language', JSON.stringify(scenario.language === 'en-US' ? 'en_US' : 'zh_CN'))
    const callbacks = new Map()
    const listeners = new Map()
    let nextID = 0
    const state = window.__referenceNative = {
        status: { ...scenario.status, lastError: null,
            controller: scenario.status.phase === 'running' ? { url: origin, secret } : null },
        config: structuredClone(scenario.config),
        dirty: false, commands: [], errors: [], blocked: [], action: null, armed: null, streamMessages: {},
        emit (event, payload) {
            for (const [id, handler] of listeners.get(event) || []) {
                callbacks.get(handler)?.({ event, id, payload: structuredClone(payload) })
            }
        },
    }
    for (const event of ['click', 'focusin', 'change']) window.addEventListener(event, input => {
        const armed = state.armed
        if (armed && input.type === armed.event && input.target instanceof Element && input.target.closest(armed.selector)) {
            state.action = { performanceMilliseconds: performance.now(), epochMilliseconds: OriginalDate.now(), event: input.type }
            state.armed = null
        }
    }, true)
    const OriginalWebSocket = WebSocket
    class ObservedWebSocket extends OriginalWebSocket {
        constructor (url, protocols) {
            const endpoint = new URL(String(url), location.href)
            if (endpoint.origin !== origin.replace('http:', 'ws:')) {
                state.blocked.push('external-websocket')
                throw new Error('External WebSockets are blocked in reference capture')
            }
            super(url, protocols)
            // Observe transport delivery without importing or changing an
            // application store. A connected socket alone is not rendered data.
            super.addEventListener('message', () => {
                state.streamMessages[endpoint.pathname] = (state.streamMessages[endpoint.pathname] || 0) + 1
            })
            this.referenceListeners = new Map()
        }
        addEventListener (type, callback, options) {
            if (type !== 'message' || !callback) return super.addEventListener(type, callback, options)
            const wrapped = event => {
                let timestamp
                try {
                    const data = JSON.parse(event.data)
                    if (typeof data.__referenceTime === 'string') {
                        const value = data.__referenceTime
                        timestamp = OriginalDate.parse(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(value) ? value.replace(' ', 'T') + 'Z' : value)
                    }
                } catch { /* Only log observations carry a display timestamp. */ }
                const previous = eventTime
                if (Number.isFinite(timestamp)) eventTime = timestamp
                try {
                    if (typeof callback === 'function') callback.call(this, event)
                    else callback.handleEvent(event)
                } finally { eventTime = previous }
            }
            this.referenceListeners.set(callback, wrapped)
            return super.addEventListener(type, wrapped, options)
        }
        removeEventListener (type, callback, options) {
            return super.removeEventListener(type, this.referenceListeners.get(callback) || callback, options)
        }
    }
    window.WebSocket = ObservedWebSocket
    window.__TAURI_INTERNALS__ = {
        metadata: { currentWindow: { label: scenario.name === 'tray' ? 'tray' : 'main' },
            currentWebview: { windowLabel: scenario.name === 'tray' ? 'tray' : 'main', label: scenario.name === 'tray' ? 'tray' : 'main' } },
        transformCallback (callback) { const id = ++nextID; callbacks.set(id, callback); return id },
        unregisterCallback (id) { callbacks.delete(id) },
        async invoke (command, args = {}) {
            if (command === 'plugin:event|listen') {
                const id = ++nextID
                if (!listeners.has(args.event)) listeners.set(args.event, new Map())
                listeners.get(args.event).set(id, args.handler)
                if (args.event === 'tray-visibility') setTimeout(() => state.emit('tray-visibility', true), 0)
                return id
            }
            if (command === 'plugin:event|unlisten') { listeners.get(args.event)?.delete(args.eventId); return }
            state.commands.push(command)
            if (command.startsWith('plugin:window|')) {
                if (command.endsWith('is_maximized')) return false
                if (command.endsWith('is_visible') || command.endsWith('is_focused')) return true
                if (command.endsWith('scale_factor')) return scenario.dpi / 96
                return
            }
            if (command === 'desktop_appearance') return structuredClone(scenario.appearance || { mica: false, accent: null })
            if (command === 'desktop_status') return structuredClone(state.status)
            if (command === 'desktop_read_config') return structuredClone(state.config)
            if (command === 'desktop_set_dirty') { state.dirty = !!args.dirty; return }
            if (command === 'desktop_patch_config') { await window.__referencePatchConfig(args.patch); return }
            if (command === 'desktop_check_update') {
                if (typeof scenario.update?.available !== 'boolean' || typeof scenario.update?.canInstall !== 'boolean') {
                    // A missing observation is not a successful update check.
                    throw { code: 'reference_observation_missing', message: 'No captured desktop update result' }
                }
                return structuredClone(scenario.update)
            }
            if (command === 'desktop_set_system_proxy') state.status.systemProxy = !!args.enabled
            else if (command === 'desktop_set_autostart') state.status.autostart = !!args.enabled
            else if (command === 'desktop_set_tun') state.status.tun = !!args.enabled
            else if (['desktop_open_directory', 'desktop_open_external', 'desktop_show_main'].includes(command)) return
            else {
                state.errors.push('Unobserved native command: ' + command)
                throw { code: 'reference_command_unobserved', message: command }
            }
            state.emit('desktop-status', state.status)
            return structuredClone(state.status)
        },
    }
    window.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener (event, id) { listeners.get(event)?.delete(id) } }
}

const selectors = [
    '.titlebar', '.titlebar-left', '.titlebar-center', '.auto-suggest-input', '.auto-suggest-list', '.auto-suggest-item',
    '.navigation-pane', '.navigation-overlay', '.page-container', '.route-view', '.page', '.header', '.home-page', '.home-hero', '.home-tiles',
    '.home-chart', '.home-stats', '.home-exits', '.home-exit', '.proxies-page', '.proxy-topology', '.proxy-group',
    '.proxy-nodes', '.settings-section', '.settings-section-title', '.setting-card', '.setting-row',
    '#set-theme', '#set-language', '#set-lan', '.button-select', '.switch', '.settings-ports',
    '.rules-card', '.rules-filter', '.rule-head', '.rule-item', '.profile-current', '.profile-current-actions',
    '.config-editor-modal', '.config-editor-text', '.config-editor-gutter', '.config-editor-footer',
    '.config-editor-confirm', '.desktop-import-modal', '.desktop-import-content', '.desktop-yaml', '.desktop-service-dialog', '.modal-container',
    '.dns-tabs', '.dns-metrics', '.dns-trend', '.dns-query-card', '.dns-pagination', '.dns-table',
    '.logs-card', '.logs-panel', '.log-line', '.logs-level-select', '.logs-paused', '.connections-table', '.connections-page',
    '.connections-rates', '.connections-rate-row', '.connections-traffic', '.connections-chart',
    '.tray-flyout', '.tray-head', '.tray-body', '.tray-mode', '.tray-tiles', '.tray-exits', '.tray-foot',
]

async function geometry (page) {
    return await page.evaluate(selectors => {
        function box (element) {
            const rect = element.getBoundingClientRect()
            const style = getComputedStyle(element)
            return { x: rect.x, y: rect.y, width: rect.width, height: rect.height, display: style.display,
                padding: style.padding, margin: style.margin, gap: style.gap, font: style.font,
                lineHeight: style.lineHeight, color: style.color, background: style.backgroundColor,
                border: style.border, borderRadius: style.borderRadius, boxShadow: style.boxShadow,
                transform: style.transform, opacity: style.opacity, scrollTop: element.scrollTop,
                scrollHeight: element.scrollHeight, clientHeight: element.clientHeight }
        }
        return {
            viewport: { width: innerWidth, height: innerHeight, devicePixelRatio },
            environment: { platform: navigator.platform, language: navigator.language, userAgent: navigator.userAgent,
                timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
                reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
                colorScheme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light' },
            fonts: { body: getComputedStyle(document.body).fontFamily,
                note: 'FontFaceSet checks report loading status, not proof that a named system font is installed; compare the same Windows host and fallback metrics.',
                loaded: [...document.fonts].map(font => ({ family: font.family, status: font.status })),
                checks: Object.fromEntries(['Segoe UI Variable', 'Segoe UI', 'Microsoft YaHei UI', 'Microsoft YaHei', 'Cascadia Mono', 'Consolas']
                    .map(family => [family, document.fonts.check('14px "' + family + '"', 'Mihomo 设置')])) },
            document: { theme: localStorage.getItem('theme'), language: localStorage.getItem('language'),
                classes: document.documentElement.className,
                accent: Object.fromEntries(['--acc-l', '--acc-d', '--acc-tl', '--acc-td'].map(key => [key, getComputedStyle(document.documentElement).getPropertyValue(key).trim()])) },
            elements: Object.fromEntries(selectors.map(selector => {
                const elements = [...document.querySelectorAll(selector)]
                return [selector, { count: elements.length, boxes: elements.slice(0, 8).map(box) }]
            })),
        }
    }, selectors)
}

const pageRoots = {
    home: '.home-page', proxies: '.proxies-page', connections: '.connections-page', dns: '.dns-page',
    rules: '.rules-page', logs: '.logs-page', profiles: '.profiles-page', settings: '.settings-page', tray: '.tray-flyout',
}

async function waitForPresentation (page, selector) {
    await page.locator(selector).waitFor({ state: 'visible' })
    // Visibility only checks the target's layout box. In particular it does not
    // wait for a route-view ancestor's opacity animation; run41 captured Home's
    // shell while its populated route was still transparent and translated.
    await page.waitForFunction(selector => {
        const target = document.querySelector(selector)
        if (!target) return false
        for (let element = target; element; element = element.parentElement) {
            const style = getComputedStyle(element)
            if (style.display === 'none' || style.visibility !== 'visible' || Number(style.opacity) < 0.999) return false
            if (element.getAnimations().some(animation => {
                const timing = animation.effect?.getComputedTiming()
                return Number.isFinite(timing?.endTime) && ['running', 'pending'].includes(animation.playState)
            })) return false
        }
        const rect = target.getBoundingClientRect()
        return rect.width > 0 && rect.height > 0
    }, selector)
    // Two paint opportunities after the final layout, without disabling,
    // seeking, or finishing the original animation on behalf of the browser.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
}

async function waitForPageReady (page, scenario, state) {
    const route = scenario.name === 'tray' ? 'tray' : scenario.page
    const selector = pageRoots[route]
    assert.ok(selector, 'No reference readiness contract for route ' + route)
    const proxies = scenario.api['/proxies'].proxies
    const policyTypes = new Set(['Selector', 'URLTest', 'Fallback', 'LoadBalance'])
    const ordered = (proxies.GLOBAL?.all || []).filter(name => !['DIRECT', 'REJECT', 'PASS', 'GLOBAL'].includes(name))
    const groups = ordered.filter(name => policyTypes.has(proxies[name]?.type))
    const exits = groups.filter(name => ['Selector', 'URLTest', 'Fallback'].includes(proxies[name]?.type)).slice(0, route === 'tray' ? 3 : 4)
    const config = scenario.api['/configs']
    const expected = {
        route, selector, phase: scenario.status.phase, exits, groups,
        renderedGroups: String(config.mode).toLowerCase() === 'global' ? ['GLOBAL', ...groups] : groups,
        mode: String(config.mode).toLowerCase(), lan: !!config['allow-lan'],
        ports: [config['mixed-port'] ?? 0, config.port ?? 0, config['socks-port'] ?? 0].map(String),
        language: scenario.language === 'en-US' ? 'en_US' : 'zh_CN',
        rules: scenario.api['/rules'].rules.length,
        configPath: scenario.status.configPath || '—',
        connections: scenario.api['/connections'].connections?.length || 0,
        connectionFrames: state.connectionFrames.length,
        logs: Math.min(scenario.streams?.logs?.length || 0, 200),
        logFrames: scenario.streams?.logs?.length || 0,
        dnsQueries: scenario.api['/dns/observability/stats']?.last_24h?.queries,
    }
    await page.locator(selector).waitFor({ state: 'visible' })
    await page.waitForFunction(expected => {
        const text = selector => document.querySelector(selector)?.textContent?.trim()
        const texts = selector => [...document.querySelectorAll(selector)].map(element => element.textContent.trim())
        const same = (actual, expected) => JSON.stringify(actual) === JSON.stringify(expected)
        const streams = window.__referenceNative.streamMessages
        if (!document.querySelector(expected.selector)) return false
        switch (expected.route) {
            case 'home':
            case 'tray': {
                const tray = expected.route === 'tray'
                const phase = document.querySelector(tray ? '.tray-flyout' : '.home-hero')?.dataset.phase
                if (phase !== expected.phase) return false
                if (phase !== 'running') return true
                return same(texts(tray ? '.tray-exit-text > b' : '.home-exit-name > b'), expected.exits) &&
                    (streams['/connections'] || 0) >= expected.connectionFrames
            }
            case 'proxies':
                return text('.proxies-summary .reading') === String(expected.groups.length) &&
                    same([...document.querySelectorAll('.proxy-group[data-group]')].map(element => element.dataset.group), expected.renderedGroups)
            case 'settings':
                return document.querySelector('#set-language [aria-pressed="true"]')?.value === expected.language &&
                    document.querySelector('#set-mode [aria-pressed="true"]')?.value === expected.mode &&
                    document.querySelector('#set-lan [role="switch"]')?.getAttribute('aria-checked') === String(expected.lan) &&
                    same(['settings-mixed-port', 'settings-http-port', 'settings-socks-port'].map(id => document.getElementById(id)?.value), expected.ports)
            case 'rules':
                return text('.rules-count .reading') === String(expected.rules) &&
                    (expected.rules === 0 || !!document.querySelector('.rule-item'))
            case 'profiles': {
                const counts = text('.profile-current-counts')?.match(/\d+/g)?.map(Number)
                return text('.profile-current-path') === expected.configPath &&
                    (expected.phase !== 'running' || same(counts, [expected.groups.length, expected.rules]))
            }
            case 'dns':
                return document.querySelector('#dns-tab-overview')?.getAttribute('aria-selected') === 'true' &&
                    text('.dns-metric strong')?.replace(/,/g, '') === String(expected.dnsQueries)
            case 'logs':
                return text('.logs-count .reading') === String(expected.logs) &&
                    document.querySelectorAll('.log-line').length === expected.logs &&
                    (streams['/logs'] || 0) >= expected.logFrames
            case 'connections':
                return text('.connections-summary .reading') === String(expected.connections) &&
                    !!document.querySelector('.connections-table') &&
                    (streams['/connections'] || 0) >= expected.connectionFrames
            default: return false
        }
    }, expected)
    if (scenario.update?.available === true && scenario.update.version && route !== 'tray') {
        await page.locator('.desktop-update-notice').waitFor({ state: 'visible' })
    }
    await waitForPresentation(page, selector)
    state.readiness = { route, selector, observedDataMatched: true, ancestorAnimationsSettled: true,
        readyAt: new Date().toISOString(), streams: await page.evaluate(() => window.__referenceNative.streamMessages) }
}

async function initializePage (scenario, origin) {
    const state = active = { scenario, api: clone(scenario.api), requests: [], streams: new Set(), caveats: [], errors: [] }
    state.connectionFrames = connectionFrames(state)
    if (!scenario.update || typeof scenario.update.available !== 'boolean') state.caveats.push('No completed desktop update observation was exported.')
    const context = await browser.newContext({ viewport: { width: scenario.width, height: scenario.height },
        deviceScaleFactor: scenario.dpi / 96, locale: scenario.language === 'en-US' ? 'en-US' : 'zh-CN',
        timezoneId: scenario.timezone || 'UTC', colorScheme: scenario.theme === 'dark' ? 'dark' : 'light',
        reducedMotion: 'no-preference', serviceWorkers: 'block' })
    await context.route('**/*', async route => {
        const url = new URL(route.request().url())
        if (url.origin === origin || ['data:', 'blob:'].includes(url.protocol)) return await route.continue()
        state.errors.push('Blocked external HTTP request: ' + url.protocol + '//' + url.hostname)
        await route.abort('blockedbyclient')
    })
    await context.exposeBinding('__referencePatchConfig', (_, patch) => {
        assert.ok(patch && typeof patch === 'object' && !Array.isArray(patch), 'Invalid native configuration patch')
        for (const [key, value] of Object.entries(patch)) {
            assert.ok(['mode', 'allow-lan', 'port', 'socks-port', 'mixed-port', 'log-level'].includes(key), 'Unexpected native patch field')
            state.api['/configs'][key] = value
        }
    })
    await context.addInitScript(initializeFixture, { scenario, origin, secret })
    const page = await context.newPage()
    page.setDefaultTimeout(12000)
    page.on('pageerror', error => state.errors.push(secretless(error.message)))
    const route = scenario.name === 'tray' ? 'tray' : scenario.page === 'home' ? '' : scenario.page
    await page.goto(origin + '/#/' + route, { waitUntil: 'networkidle' })
    await page.evaluate(async () => { await document.fonts.ready })
    await page.locator(scenario.name === 'tray' ? '.tray-flyout' : '.titlebar').waitFor({ state: 'visible' })
    await page.waitForFunction(() => window.__referenceNative?.commands.includes('desktop_status'))
    await waitForPageReady(page, scenario, state)
    return { context, page, state }
}

async function configureScenario (page, scenario) {
    let presentation = pageRoots[scenario.name === 'tray' ? 'tray' : scenario.page]
    if (scenario.name === 'search') {
        const input = page.locator('.titlebar .auto-suggest-input')
        await input.focus()
        await input.fill(scenario.searchQuery || '')
        await page.locator('.auto-suggest-list').waitFor({ state: 'visible' })
        await page.waitForFunction(query => document.querySelector('.auto-suggest-input')?.value === query &&
            document.querySelectorAll('.auto-suggest-item').length > 0, scenario.searchQuery || '')
        presentation = '.auto-suggest-list'
    } else if (scenario.name === 'rules') {
        // Use the same title-bar search navigation as the native scenario. In
        // the original this sets the full rule payload without focusing or
        // selecting the Rules input, so directly filling it would change focus.
        const query = scenario.ruleQuery || ''
        const rule = scenario.api['/rules'].rules.find(item => item.payload === query) ||
            scenario.api['/rules'].rules.find(item => item.payload?.includes(query))
        assert.ok(rule?.payload, 'The observed rule search target is missing')
        const input = page.locator('.titlebar .auto-suggest-input')
        await input.fill(rule.payload)
        const result = page.locator('.auto-suggest-item').filter({
            has: page.locator('.auto-suggest-text > b').filter({ hasText: rule.payload }),
        }).first()
        await result.click()
        await page.waitForFunction(payload => document.querySelector('.rules-filter input')?.value === payload, rule.payload)
        await page.locator('.rule-item .payload').filter({ hasText: rule.payload }).first().waitFor({ state: 'visible' })
    } else if (scenario.name === 'tun-consent') {
        await page.locator('[data-tile="tun"] .home-tile-main').click()
        await page.locator('.desktop-service-dialog').waitFor({ state: 'visible' })
        presentation = '.desktop-service-dialog'
    } else if (scenario.name === 'import-config') {
        await page.getByRole('button', { name: scenario.language === 'en-US' ? 'Paste YAML' : '粘贴 YAML', exact: true }).click()
        const editor = page.locator('.desktop-import-modal .desktop-yaml')
        await editor.waitFor({ state: 'visible' })
        await editor.fill(scenario.import?.content ?? '')
        await editor.evaluate(element => { element.setSelectionRange(0, 0); element.scrollTop = 0 })
        presentation = '.desktop-import-modal'
    } else if (scenario.name === 'config-editor' || scenario.name === 'config-unsaved') {
        await page.locator('.profile-current-actions button').first().click()
        const editor = page.locator('.config-editor-text')
        await editor.waitFor({ state: 'visible' })
        await editor.fill(scenario.editor?.content ?? scenario.config.content)
        await editor.evaluate((element, selection) => {
            element.setSelectionRange(selection.start, selection.start + selection.length)
            // React's onSelect does not subscribe to a textarea's native
            // `select` event. Use the original editor's ordinary key-up tracker
            // after restoring the observed selection, so the visible line/column
            // describes the caret we actually capture rather than fill()'s end.
            element.dispatchEvent(new KeyboardEvent('keyup', { key: 'Shift', bubbles: true }))
            element.scrollTop = 0
        }, { start: scenario.editor?.selectionStart || 0, length: scenario.editor?.selectionLength || 0 })
        if (scenario.name === 'config-unsaved') {
            await page.locator('.config-editor-modal .subpage-back').click()
            await page.locator('.config-editor-confirm').waitFor({ state: 'visible' })
        }
        presentation = scenario.name === 'config-unsaved' ? '.config-editor-confirm' : '.config-editor-modal'
    } else if (scenario.name === 'dns-queries') {
        await page.locator('#dns-tab-queries').click()
        await page.locator('.dns-pagination select').selectOption('100')
        await page.waitForFunction(expected => document.querySelector('.dns-pagination select')?.value === '100' &&
            !document.querySelector('.dns-query-card [aria-busy="true"]') &&
            document.querySelectorAll('.dns-query-card tbody tr:not(:has(.dns-empty))').length === expected,
        scenario.api['/dns/observability/queries'].items.length)
        presentation = '.dns-query-card'
    } else if (scenario.navigationOverlay) {
        await page.locator('.titlebar-left > button').nth(1).click()
        await page.locator('.app.nav-open').waitFor({ state: 'visible' })
        presentation = '.nav-slot > .sidebar'
    }
    await waitForPresentation(page, presentation)
}

async function validatePage (page, state) {
    const native = await page.evaluate(() => ({
        errors: window.__referenceNative.errors, blocked: window.__referenceNative.blocked,
        commands: window.__referenceNative.commands,
    }))
    state.errors.push(...native.errors, ...native.blocked.map(value => 'Blocked ' + value))
    assert.equal(state.errors.length, 0, 'Reference scenario ' + state.scenario.name + ' failed: ' + state.errors.join('; '))
    return native
}

async function captureStatic (scenario, origin) {
    const { context, page, state } = await initializePage(scenario, origin)
    try {
        await configureScenario(page, scenario)
        const native = await validatePage(page, state)
        const file = 'reference-' + scenario.name + '.png'
        await page.screenshot({ path: path.join(output, file), animations: 'allow', caret: 'initial' })
        const metrics = await geometry(page)
        report.scenarios.push({ name: scenario.name, file, sha256: sha256(fs.readFileSync(path.join(output, file))),
            capturedAt: new Date().toISOString(), observedAt: scenario.capturedAt,
            width: scenario.width, height: scenario.height, dpi: scenario.dpi, geometry: metrics,
            traffic: state.traffic, logs: state.logs, readiness: state.readiness, requests: state.requests, streams: [...state.streams],
            commands: native.commands, caveats: state.caveats })
        checkpoint()
        console.log('reference: ' + scenario.name)
    } finally {
        await context.close()
        for (const socket of sockets) socket.terminate()
    }
}

function actionLocator (page, scenario) {
    switch (scenario.name) {
        case 'home-to-proxies': {
            const group = scenario.action.automationId.replace(/^HomeExitSummary_/, '')
            const locator = page.locator('.home-exit').filter({ has: page.locator('.home-exit-name > b').filter({ hasText: group }) }).first()
            return { locator, selector: '.home-exit', event: 'click' }
        }
        case 'settings-language':
            return { locator: page.locator('#set-language .button-select-options').filter({ hasText: scenario.action.value }),
                selector: '#set-language .button-select-options', event: 'click' }
        case 'search-open':
            return { locator: page.locator('.titlebar .auto-suggest-input'), selector: '.titlebar .auto-suggest-input', event: 'focusin', focus: true }
        case 'settings-switch':
            return { locator: page.locator('#set-lan [role="switch"]'), selector: '#set-lan [role="switch"]', event: 'click' }
        case 'editor-open':
            return { locator: page.locator('.profile-current-actions button').first(), selector: '.profile-current-actions button', event: 'click' }
        case 'narrow-navigation':
            return { locator: page.locator('.titlebar-left > button').nth(1), selector: '.titlebar-left > button', event: 'click' }
        default: throw new Error('Unmapped reference motion')
    }
}

async function captureMotion (scenario, origin) {
    const { context, page, state } = await initializePage(scenario, origin)
    const directory = path.join(output, 'motion')
    fs.mkdirSync(directory, { recursive: true })
    let cdp
    try {
        const action = actionLocator(page, scenario)
        await action.locator.waitFor({ state: 'visible' })
        await action.locator.scrollIntoViewIfNeeded()
        await waitForPresentation(page, pageRoots[scenario.page])
        cdp = await context.newCDPSession(page)
        const frames = []
        let ackError
        cdp.on('Page.screencastFrame', event => {
            if (frames.length < 256) frames.push({ data: event.data, epochMilliseconds: event.metadata.timestamp * 1000,
                receivedEpochMilliseconds: Date.now(), metadata: event.metadata })
            cdp.send('Page.screencastFrameAck', { sessionId: event.sessionId }).catch(error => { ackError = error })
        })
        await cdp.send('Page.enable')
        await cdp.send('Page.startScreencast', { format: 'png', everyNthFrame: 1,
            maxWidth: Math.ceil(scenario.width * scenario.dpi / 96), maxHeight: Math.ceil(scenario.height * scenario.dpi / 96) })
        const initialDeadline = Date.now() + 5000
        while (!frames.length && Date.now() < initialDeadline) await delay(25)
        assert.ok(frames.length, 'The reference browser did not provide an initial screencast frame')
        await page.evaluate(armed => {
            window.__referenceNative.action = null
            window.__referenceNative.armed = armed
        }, { selector: action.selector, event: action.event })
        if (action.focus) await action.locator.focus()
        else await action.locator.click()
        await page.waitForFunction(() => window.__referenceNative.action !== null)
        const triggered = await page.evaluate(() => window.__referenceNative.action)
        await page.waitForTimeout(Math.max(0, triggered.epochMilliseconds + targets.at(-1) + 180 - Date.now()))
        await cdp.send('Page.stopScreencast')
        assert.ok(!ackError, 'The reference screencast frame acknowledgement failed')
        assert.ok(frames.length >= 2, 'No frames arrived after the real reference interaction')
        await validatePage(page, state)
        const selected = targets.map((target, index) => {
            let best = 0
            for (let at = 1; at < frames.length; at++) {
                if (Math.abs(frames[at].epochMilliseconds - triggered.epochMilliseconds - target) <
                    Math.abs(frames[best].epochMilliseconds - triggered.epochMilliseconds - target)) best = at
            }
            const frame = frames[best]
            const file = scenario.name + '-' + String(index).padStart(2, '0') + '.png'
            const bytes = Buffer.from(frame.data, 'base64')
            fs.writeFileSync(path.join(directory, file), bytes)
            const elapsed = frame.epochMilliseconds - triggered.epochMilliseconds
            return { file, targetMilliseconds: target, actualElapsedMilliseconds: elapsed,
                errorMilliseconds: elapsed - target, receivedElapsedMilliseconds: frame.receivedEpochMilliseconds - triggered.epochMilliseconds,
                sourceFrame: best, sha256: sha256(bytes), metadata: frame.metadata }
        })
        const result = { schema: 1, name: scenario.name, kind: 'real-cdp-screencast',
            trigger: { ...scenario.action, browserEvent: triggered.event }, actionEpochMilliseconds: triggered.epochMilliseconds,
            actionPerformanceMilliseconds: triggered.performanceMilliseconds, targetsMilliseconds: targets,
            recordedFrames: frames.length, selectedUniqueFrames: new Set(selected.map(frame => frame.sourceFrame)).size,
            frames: selected, readiness: state.readiness, caveats: state.caveats,
            timing: 'Closest actual compositor frame to each requested time. Reused frames and timing error are explicit; serial screenshots do not drive or pause the animation.',
            geometryAfter: await geometry(page) }
        const manifest = 'motion/' + scenario.name + '.json'
        fs.writeFileSync(path.join(output, manifest), JSON.stringify(result, null, 2) + '\n')
        report.motions.push({ name: scenario.name, manifest, recordedFrames: frames.length,
            selectedUniqueFrames: result.selectedUniqueFrames,
            maximumAbsoluteTimingErrorMilliseconds: Math.max(...selected.map(frame => Math.abs(frame.errorMilliseconds))) })
        checkpoint()
        console.log('reference motion: ' + scenario.name + ' (' + frames.length + ' actual frames)')
    } finally {
        if (cdp) await cdp.detach().catch(() => {})
        await context.close()
        for (const socket of sockets) socket.terminate()
    }
}

async function main () {
    assert.ok(fs.existsSync(path.join(dist, 'index.html')), 'Build the unchanged reference desktop frontend first')
    const bytes = fs.readFileSync(fixtureFile)
    assert.ok(bytes.length <= 32 * 1024 * 1024, 'Reference fixture exceeds its bounded size')
    report.fixtureSHA256 = sha256(bytes)
    const fixture = JSON.parse(bytes.toString('utf8'))
    assert.equal(fixture.schema, 1, 'Unsupported reference fixture schema')
    assert.ok(Array.isArray(fixture.scenarios), 'Missing reference scenarios')
    const names = fixture.scenarios.map(item => item.name)
    assert.equal(new Set(names).size, names.length, 'Duplicate reference scenario')
    assert.ok(names.length > 0 && names.every(name => requiredScenarios.includes(name)), 'Unknown or empty reference scenarios')
    if (!partial) assert.deepEqual([...names].sort(), [...requiredScenarios].sort(), 'The full sixteen-view capture is required')
    for (const scenario of [...fixture.scenarios, ...(fixture.motions || [])]) validateScenario(scenario)
    const referenceRequire = createRequire(path.join(source, 'package.json'))
    const { chromium } = referenceRequire('playwright')
    const { WebSocketServer } = referenceRequire('ws')
    const executablePath = browserPath(chromium)
    const origin = await createServer(WebSocketServer)
    browser = await chromium.launch({ headless: true, executablePath, ignoreDefaultArgs: ['--hide-scrollbars'],
        args: ['--no-sandbox', '--disable-background-networking', '--disable-component-update', '--disable-features=MediaRouter'] })
    report.browser = { version: browser.version(), executable: path.basename(executablePath), platform: process.platform,
        sourceIndexSHA256: sha256(fs.readFileSync(path.join(dist, 'index.html'))) }
    checkpoint()
    for (const scenario of fixture.scenarios) await captureStatic(scenario, origin)
    fs.writeFileSync(path.join(output, 'reference-geometry.json'), JSON.stringify({
        schema: 1, browser: report.browser, fixtureSHA256: report.fixtureSHA256,
        scenarios: Object.fromEntries(report.scenarios.map(item => [item.name, item.geometry])),
    }, null, 2) + '\n')
    if (process.env.REFERENCE_SKIP_MOTION !== '1') {
        const motionNames = (fixture.motions || []).map(item => item.name)
        assert.equal(new Set(motionNames).size, motionNames.length, 'Duplicate reference motion')
        assert.ok(motionNames.every(name => requiredMotions.includes(name)), 'Unknown reference motion')
        if (!partial) assert.deepEqual([...motionNames].sort(), [...requiredMotions].sort(), 'The full six-interaction motion capture is required')
        for (const scenario of fixture.motions || []) await captureMotion(scenario, origin)
    } else report.motionSkipped = 'Explicit REFERENCE_SKIP_MOTION=1; this run is not complete motion evidence.'
    report.completed = !partial && report.scenarios.length === requiredScenarios.length && report.motions.length === requiredMotions.length
    checkpoint()
    console.log(JSON.stringify({ output: path.relative(root, output), captures: report.scenarios.length,
        motions: report.motions.length, errors: report.errors.length, completed: report.completed }))
}

main().catch(error => {
    const message = secretless(error.stack || error.message)
    report.errors.push(message)
    checkpoint()
    console.error(message)
    process.exitCode = 1
}).finally(async () => {
    if (browser) await browser.close().catch(() => {})
    for (const socket of sockets) socket.terminate()
    if (websocketServer) websocketServer.close()
    if (server) { server.closeAllConnections(); server.close() }
})
