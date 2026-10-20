#!/usr/bin/env node
// Run the actual desktop build in Chromium with a deterministic native IPC host.
// This tests the UI/IPC contract; Windows service/UAC and installer tests run separately.
const assert = require('node:assert/strict')
const fs = require('node:fs')
const http = require('node:http')
const path = require('node:path')
const { chromium } = require('playwright')
const { WebSocketServer } = require('ws')

const dist = path.resolve(process.env.UI_DIST_DIR || path.join(__dirname, '../..', 'dist-desktop'))
const secret = 'fixture-random-native-controller-secret'
const requests = []
const sockets = new Set()
const pageErrors = []
let browser
let server
let tick

function json(res, value) {
    res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' })
    res.end(JSON.stringify(value))
}

async function until(predicate, message) {
    const deadline = Date.now() + 7000
    while (!await predicate() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 25))
    assert.ok(await predicate(), message)
}

async function verifyStartupUpdates(origin, initializeFixture) {
    async function open(fixtureCase, route = '/connections') {
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'zh-CN', reducedMotion: 'reduce' })
        await context.addInitScript(initializeFixture, { origin, secret })
        await context.route('**/*', route => {
            if (route.request().url().startsWith(origin)) return route.continue()
            pageErrors.push('Unexpected external request: ' + route.request().url())
            return route.abort()
        })
        const page = await context.newPage()
        page.setDefaultTimeout(7000)
        page.on('pageerror', error => pageErrors.push(error.message))
        await page.clock.install({ time: new Date('2026-10-19T00:00:00Z') })
        await page.clock.pauseAt(new Date('2026-10-19T00:00:01Z'))
        await page.goto(origin + '/?fixture=' + fixtureCase + '#' + route)
        await until(() => page.evaluate(() => window.__desktopFixture.commands.some(item => item.command === 'desktop_status')), 'startup reads native status')
        await page.clock.runFor(100)
        return { context, page }
    }
    const count = (page, command) => page.evaluate(command => window.__desktopFixture.commands.filter(item => item.command === command).length, command)
    async function route(page, target) {
        await page.evaluate(target => { location.hash = '#' + target }, target)
        await page.clock.runFor(100)
    }
    async function noInstall(page) {
        assert.equal(await count(page, 'desktop_install_update'), 0, 'checking never installs an update')
        assert.equal(await count(page, 'desktop_open_external'), 0, 'checking never opens a download page')
    }

    // The native host may start before the WebView subscribes. A pending first
    // snapshot must not turn an unknown runtime into a stopped or failed one.
    {
        const { context, page } = await open('startup-status-held')
        try {
            await until(() => page.evaluate(() => typeof window.__desktopFixture.finishStatus === 'function'), 'the initial native status is pending')
            const pending = {
                title: await page.locator('.desktop-recovery h1').innerText(),
                hint: await page.locator('.desktop-recovery > p').first().innerText(),
                proxyValues: await page.locator('.desktop-status-details b').allTextContents(),
            }
            await route(page, '/settings')
            Object.assign(pending, {
                errorPhase: await page.locator('.desktop-controls .desktop-phase.is-error').count(),
                service: await page.locator('.desktop-service-status').innerText(),
                startEnabled: await page.getByRole('button', { name: '启动内核', exact: true }).isEnabled(),
                restartEnabled: await page.getByRole('button', { name: '重启内核', exact: true }).isEnabled(),
                refreshEnabled: await page.getByRole('button', { name: '刷新状态', exact: true }).isEnabled(),
            })
            assert.deepEqual(pending, {
                title: '正在连接桌面服务…', hint: '正在读取本机内核状态，请稍候。', proxyValues: ['—', '—'],
                errorPhase: 0, service: '—', startEnabled: false, restartEnabled: false, refreshEnabled: false,
            }, 'a late first snapshot is unknown, with no false failure or queued runtime controls')
            await page.clock.runFor(180000)
            for (const command of ['desktop_start', 'desktop_restart', 'desktop_check_update']) {
                assert.equal(await count(page, command), 0, `waiting for the initial status does not submit ${command}`)
            }
            assert.equal(await count(page, 'desktop_status'), 1, 'pending initialization does not queue redundant status reads')
            await page.evaluate(() => { window.__desktopFixture.holdStatus = false; window.__desktopFixture.finishStatus() })
            await route(page, '/connections')
            await page.getByRole('heading', { name: '正在启动本机内核', exact: true }).waitFor()
            assert.equal(await page.locator('.desktop-recovery-page').getAttribute('data-phase'), 'starting')
            await page.getByText('正在校验配置、准备所需资源并等待内核就绪。首次启动可能需要更长时间，请稍候。', { exact: true }).waitFor()
            assert.equal(await page.getByRole('button', { name: '启动内核', exact: true }).isEnabled(), false)
            await route(page, '/settings')
            assert.equal(await page.getByRole('button', { name: '重启内核', exact: true }).isEnabled(), false)
            await page.getByRole('button', { name: 'English', exact: true }).click()
            await route(page, '/connections')
            await page.getByRole('heading', { name: 'Starting the local core', exact: true }).waitFor()
            await page.getByText('Validating configuration, preparing required resources, and waiting for the core to become ready. The first start may take longer. Please wait.', { exact: true }).waitFor()
            await page.evaluate(() => window.__desktopFixture.phase('running'))
            await until(() => page.locator('.desktop-recovery-page').count().then(value => value === 0), 'native readiness opens the dashboard without a manual restart')
            assert.equal(await count(page, 'desktop_start'), 0)
            assert.equal(await count(page, 'desktop_restart'), 0)
            await page.evaluate(() => window.__desktopFixture.phase('stopping'))
            await page.getByRole('heading', { name: 'Stopping the local core', exact: true }).waitFor()
            assert.equal(await page.getByRole('button', { name: 'Start core', exact: true }).isEnabled(), false)
            await page.evaluate(() => window.__desktopFixture.phase('stopped'))
            await page.getByRole('heading', { name: 'The local core is not running', exact: true }).waitFor()
            assert.equal(await page.getByRole('button', { name: 'Start core', exact: true }).isEnabled(), true)
            await page.evaluate(() => {
                window.__desktopFixture.status.lastError = 'Fixture resource preparation failure'
                window.__desktopFixture.phase('error')
            })
            await page.getByRole('heading', { name: 'The local core encountered a problem', exact: true }).waitFor()
            assert.equal(await page.locator('.desktop-recovery [role="alert"]').innerText(), 'Fixture resource preparation failure')
            await route(page, '/settings')
            assert.equal(await page.getByRole('button', { name: 'Log folder', exact: true }).isEnabled(), true, 'real errors preserve access to native logs')
            assert.equal(await page.getByRole('button', { name: 'Edit configuration file', exact: true }).isEnabled(), true, 'real errors preserve offline configuration editing')
            await page.evaluate(() => window.__desktopFixture.phase('running'))
            assert.equal(await page.locator('.desktop-controls [role="alert"]').innerText(), 'Fixture resource preparation failure', 'running status does not hide a separate native error')
            await page.getByRole('button', { name: '中文', exact: true }).click()
            await page.evaluate(() => window.__desktopFixture.phase('error'))
            await route(page, '/connections')
            await page.getByRole('heading', { name: '本机内核运行遇到问题', exact: true }).waitFor()
            await page.evaluate(() => window.__desktopFixture.phase('stopped'))
            assert.equal(await page.locator('.desktop-recovery [role="alert"]').innerText(), 'Fixture resource preparation failure', 'stopped status does not hide a retained native error')
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('startup-status-failed')
        try {
            await page.getByRole('heading', { name: '无法读取本机状态', exact: true }).waitFor()
            assert.equal(await page.locator('.desktop-statusbar .desktop-phase.is-error').count(), 1, 'a failed first status read is distinct from a pending read')
            assert.equal(await page.getByRole('button', { name: '启动内核', exact: true }).isEnabled(), false)
            await page.evaluate(() => { window.__desktopFixture.statusReadError = false })
            await page.getByRole('button', { name: '刷新状态', exact: true }).click()
            await page.getByRole('heading', { name: '正在启动本机内核', exact: true }).waitFor()
            assert.equal(await count(page, 'desktop_status'), 2, 'retry explicitly reads a fresh native status')
            assert.equal(await count(page, 'desktop_start'), 0, 'status recovery never starts the core again')
        } finally { await context.close() }
    }

    // Hold the real UI's IPC response to expose duplicate requests and accidental
    // coupling between a background check, core startup, and route lifetimes.
    {
        const { context, page } = await open('update-held')
        try {
            assert.equal(await count(page, 'desktop_check_update'), 0, 'initial rendering does not wait for a release request')
            await page.clock.runFor(6000)
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), 'startup checks without opening Settings')
            await noInstall(page)
            await page.getByRole('button', { name: '启动内核', exact: true }).first().click()
            await until(() => page.evaluate(() => window.__desktopFixture.status.phase === 'running'), 'a slow update check does not block core startup')
            await route(page, '/settings')
            assert.equal(await page.getByRole('button', { name: '检查应用更新', exact: true }).isEnabled(), false, 'manual check shares the pending startup request')
            await route(page, '/connections')
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 1, 'route changes and elapsed time do not duplicate a pending check')
            await page.evaluate(() => { window.__desktopFixture.holdUpdateCheck = false; window.__desktopFixture.finishUpdateCheck() })
            const notice = page.locator('.desktop-update-notice')
            await notice.waitFor()
            assert.ok((await notice.innerText()).includes('0.2.0'), 'the global notice identifies the available version')
            await noInstall(page)
            if (process.env.DESKTOP_UPDATE_SCREENSHOT) await page.screenshot({ path: process.env.DESKTOP_UPDATE_SCREENSHOT, fullPage: true })
            await notice.getByRole('button', { name: '查看更新', exact: true }).click()
            await page.clock.runFor(100)
            await page.getByRole('button', { name: '安装并重新启动', exact: true }).waitFor()
            assert.equal(await count(page, 'desktop_check_update'), 1, 'opening update details reuses the startup result')
            const cardInView = await page.locator('.desktop-app-update').evaluate(element => {
                const rect = element.getBoundingClientRect()
                return rect.top < innerHeight && rect.bottom > 50
            })
            assert.equal(cardInView, true, 'View update scrolls the result into the visible settings area')
            await route(page, '/connections')
            await page.clock.runFor(60000)
            assert.equal(await notice.count(), 0, 'a viewed version does not reappear after navigation')
            await route(page, '/settings')
            assert.equal(await page.getByRole('button', { name: '安装并重新启动', exact: true }).isVisible(), true, 'dismissing the notice preserves the update in Settings')
            await page.evaluate(() => { window.__desktopFixture.holdUpdateInstall = true })
            await page.getByRole('button', { name: '安装并重新启动', exact: true }).click()
            await until(() => count(page, 'desktop_install_update').then(value => value === 1), 'installation requires an explicit click')
            await route(page, '/connections')
            await route(page, '/settings')
            assert.equal(await page.getByRole('button', { name: '检查应用更新', exact: true }).isEnabled(), false, 'leaving Settings cannot bypass an active installation')
            assert.equal(await page.getByRole('button', { name: '安装并重新启动', exact: true }).isEnabled(), false, 'an installation cannot be submitted twice')
            assert.equal(await count(page, 'desktop_check_update'), 1, 'installation cannot race another metadata check')
            await page.evaluate(() => { window.__desktopFixture.finishUpdateInstall() })
            await until(() => page.getByRole('button', { name: '检查应用更新', exact: true }).isEnabled(), 'the shared update operation finishes')
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-failure')
        try {
            await page.clock.runFor(6000)
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), 'startup attempts a check while the update network is unavailable')
            assert.equal(await page.locator('.desktop-update-notice').count(), 0, 'an automatic failure does not show a new-version notice')
            assert.equal(await page.getByText('Fixture update network failure', { exact: true }).count(), 0, 'automatic failure does not become a core error or popup')
            await page.getByRole('button', { name: '启动内核', exact: true }).first().click()
            await until(() => page.evaluate(() => window.__desktopFixture.status.phase === 'running'), 'core controls still work after an update network failure')
            await page.evaluate(() => {
                window.__desktopFixture.emit('desktop-visibility', { visible: false })
                window.__desktopFixture.emit('desktop-visibility', { visible: true })
            })
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 1, 'a failed startup check is not retried on a timer or tray show')
            await route(page, '/settings')
            await page.locator('.desktop-app-update').getByText('Fixture update network failure', { exact: true }).waitFor()
            await page.evaluate(() => { window.__desktopFixture.updateError = false })
            await page.getByRole('button', { name: '检查应用更新', exact: true }).click()
            await page.getByRole('button', { name: '安装并重新启动', exact: true }).waitFor()
            assert.equal(await count(page, 'desktop_check_update'), 2, 'a failed startup check can be retried manually')
            await noInstall(page)
            await page.evaluate(() => { window.__desktopFixture.updateError = true })
            await page.getByRole('button', { name: '检查应用更新', exact: true }).click()
            await page.locator('.desktop-app-update').getByText('Fixture update network failure', { exact: true }).waitFor()
            assert.equal(await page.getByRole('button', { name: '安装并重新启动', exact: true }).count(), 0, 'a failed recheck cannot install a cleared native candidate')
            assert.equal(await page.locator('.desktop-update-notice').count(), 0, 'a failed recheck clears a stale available-version notice')
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-manual', '/settings')
        try {
            await page.getByRole('button', { name: '检查应用更新', exact: true }).click()
            await page.getByRole('button', { name: '安装并重新启动', exact: true }).waitFor()
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 1, 'a manual check before the startup timer consumes this launch check')
            await noInstall(page)
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-none')
        try {
            await page.clock.runFor(6000)
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), 'startup checks an already-current application')
            assert.equal(await page.locator('.desktop-update-notice').count(), 0, 'the latest version does not display an update prompt')
            await route(page, '/settings')
            await page.locator('.desktop-app-update').getByText('已是最新版本', { exact: true }).waitFor()
            await noInstall(page)
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-starting')
        try {
            await page.clock.runFor(10000)
            assert.equal(await count(page, 'desktop_check_update'), 0, 'automatic checks wait for a starting core to settle')
            await page.evaluate(() => window.__desktopFixture.phase('running'))
            await page.clock.runFor(2000)
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), 'the settled core allows the single startup check')
            await page.locator('.desktop-update-notice').waitFor()
            await page.getByRole('button', { name: '关闭更新提示', exact: true }).click()
            await route(page, '/settings')
            await route(page, '/connections')
            assert.equal(await page.locator('.desktop-update-notice').count(), 0, 'closing the notice suppresses that version for the rest of this launch')
            assert.equal(await count(page, 'desktop_check_update'), 1, 'closing a notice never schedules another request')
            await noInstall(page)
        } finally { await context.close() }
    }

    for (const phase of ['running', 'error']) {
        const { context, page } = await open('update-starting')
        try {
            await page.clock.runFor(180000)
            assert.equal(await count(page, 'desktop_check_update'), 0, 'resource preparation does not race a release request')
            await page.evaluate(phase => window.__desktopFixture.phase(phase), phase)
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), `long preparation ending in ${phase} resumes the startup check`)
            await page.evaluate(() => {
                window.__desktopFixture.emit('desktop-visibility', { visible: false })
                window.__desktopFixture.emit('desktop-visibility', { visible: true })
                window.__desktopFixture.emit('desktop-status', window.__desktopFixture.status)
            })
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 1, 'later status events and elapsed time do not repeat the automatic check')
            await noInstall(page)
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-start-held')
        try {
            await page.getByRole('button', { name: '启动内核', exact: true }).click()
            await until(() => page.evaluate(() => typeof window.__desktopFixture.finishStart === 'function'), 'the native start is pending')
            await page.evaluate(() => window.__desktopFixture.phase('running'))
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 0, 'a status event cannot bypass an unfinished core operation')
            await page.evaluate(() => window.__desktopFixture.finishStart())
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), 'finishing the core operation releases the pending check')
            await noInstall(page)
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-write-held', '/settings')
        try {
            await page.getByRole('button', { name: '编辑配置文件', exact: true }).click()
            const editor = page.locator('textarea.config-editor-text')
            await until(() => editor.isEditable(), 'the native editor opens before the startup check')
            await editor.fill('mode: direct\n# pending save\n')
            await page.locator('.config-editor-footer').getByRole('button', { name: '保存并重新加载', exact: true }).click()
            await until(() => page.evaluate(() => typeof window.__desktopFixture.finishConfigSave === 'function'), 'the native configuration write is pending')
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 0, 'automatic updates wait for an active configuration write')
            await page.evaluate(() => window.__desktopFixture.finishConfigSave())
            await until(() => count(page, 'desktop_check_update').then(value => value === 1), 'saving and applying the configuration releases the pending check')
            await noInstall(page)
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-starting', '/settings')
        try {
            await page.clock.runFor(60000)
            await page.getByRole('button', { name: '检查应用更新', exact: true }).click()
            await page.locator('.desktop-app-update').getByText('发现新版本 · 0.2.0', { exact: true }).waitFor()
            await page.evaluate(() => window.__desktopFixture.phase('running'))
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 1, 'a manual check consumes the pending automatic check during long preparation')
            await noInstall(page)
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-starting')
        try {
            await page.clock.runFor(60000)
            await page.evaluate(() => {
                window.dispatchEvent(new PageTransitionEvent('pagehide'))
                window.__desktopFixture.phase('running')
            })
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 0, 'leaving the WebView cancels a waiting startup check')
        } finally { await context.close() }
    }

    {
        const { context, page } = await open('update-exit', '/settings')
        try {
            await page.getByRole('button', { name: '编辑配置文件', exact: true }).click()
            const editor = page.locator('textarea.config-editor-text')
            await until(() => editor.isEditable(), 'the exit test opens a native draft')
            await editor.fill('mode: direct\n# unsaved exit draft\n')
            await page.evaluate(() => window.__desktopFixture.emit('desktop-exit-requested', {}))
            await page.getByRole('button', { name: '丢弃修改并退出', exact: true }).click()
            await until(() => page.evaluate(() => window.__desktopFixture.exited), 'explicit exit reaches the native host')
            await page.clock.runFor(60000)
            assert.equal(await count(page, 'desktop_check_update'), 0, 'confirmed exit cancels the initial delay before the WebView closes')
        } finally { await context.close() }
    }
    process.stdout.write('Desktop startup update regression passed: one check per launch, background core access, shared route state, notice/dismissal, failure/retry, installation exclusion, manual-first, long preparation/write recovery and exit cancellation.\n')
}

async function main() {
    assert.ok(fs.existsSync(path.join(dist, 'index.html')), 'build the desktop UI first')
    const ws = new WebSocketServer({ noServer: true })
    const resourceTask = { instance_id: 'fixture-instance', state: 'idle', items: [
        { id: 'kernel', kind: 'kernel', name: 'kernel', status: 'skipped', reason: 'desktop_managed' },
        { id: 'ui', kind: 'ui', name: 'ui', status: 'skipped', reason: 'desktop_managed' },
        { id: 'geodata', kind: 'geodata', name: 'GeoIP', status: 'up_to_date' },
    ] }
    server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://127.0.0.1')
        res.setHeader('Access-Control-Allow-Origin', '*')
        res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type, Idempotency-Key, X-Mihomo-Instance-ID')
        res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PATCH, PUT, OPTIONS')
        if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return }
        const api = {
            '/version': { version: 'mihomo-optimized-v1.0.17-desktop', premium: true },
            '/configs': { port: 7890, 'socks-port': 7891, 'mixed-port': 7892, 'redir-port': 0, mode: 'rule', 'log-level': 'info', 'allow-lan': false },
            '/providers/proxies': { providers: {} }, '/providers/rules': { providers: {} }, '/rules': { rules: [] },
            '/proxies': { proxies: {
                GLOBAL: { name: 'GLOBAL', type: 'Selector', now: 'fixture-node', history: [], all: ['fixture-node', 'select'] },
                select: { name: 'select', type: 'Selector', now: 'fixture-node', history: [], all: ['fixture-node'] },
                'fixture-node': { name: 'fixture-node', type: 'Socks', history: [], udp: true },
            } },
            '/connections': { connections: [], uploadTotal: 0, downloadTotal: 0 },
            '/upgrade/check': resourceTask,
        }
        if (api[url.pathname]) {
            requests.push({ path: url.pathname, method: req.method, auth: req.headers.authorization })
            assert.equal(req.headers.authorization, `Bearer ${secret}`, 'HTTP uses only the native secret')
            if (url.pathname === '/upgrade/check' && req.method === 'POST') {
                Object.assign(resourceTask, { state: 'running', task_id: 'fixture-resource-update', request_id: req.headers['idempotency-key'] })
                resourceTask.items.find(item => item.kind === 'geodata').status = 'checking'
            }
            json(res, api[url.pathname]); return
        }
        if (url.pathname === '/configs/file') {
            requests.push({ path: url.pathname })
            res.writeHead(500); res.end('Configuration must use native IPC'); return
        }
        const file = path.resolve(dist, '.' + (url.pathname === '/' ? '/index.html' : url.pathname))
        if (!file.startsWith(dist + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return }
        const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.png': 'image/png', '.svg': 'image/svg+xml' }
        res.writeHead(200, { 'content-type': types[path.extname(file)] || 'application/octet-stream' })
        fs.createReadStream(file).pipe(res)
    })
    server.on('upgrade', (req, socket, head) => {
        const url = new URL(req.url, 'http://127.0.0.1')
        if (url.searchParams.get('token') !== secret) { socket.destroy(); return }
        ws.handleUpgrade(req, socket, head, stream => {
            stream.kind = url.pathname
            sockets.add(stream)
            stream.on('close', () => sockets.delete(stream))
            if (stream.kind === '/logs') stream.send(JSON.stringify({ type: 'info', payload: 'Desktop fixture ready' }))
        })
    })
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
    const origin = `http://127.0.0.1:${server.address().port}`
    let sequence = 0
    tick = setInterval(() => {
        for (const socket of sockets) {
            if (socket.readyState === 1 && socket.kind === '/connections') socket.send(JSON.stringify({
                connections: [], uploadTotal: sequence * 100, downloadTotal: sequence * 200,
                sequence, sampledAt: sequence * 250,
            }))
        }
        sequence++
    }, 250)

    browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || undefined, args: ['--no-sandbox'] })
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'zh-CN', reducedMotion: 'reduce' })
    const initializeFixture = ({ origin, secret }) => {
        localStorage.setItem('language', JSON.stringify('zh_CN'))
        localStorage.setItem('externalControllers', JSON.stringify([{ hostname: 'untrusted.invalid', port: 9999, secret: 'untrusted-storage' }]))
        const fixtureCase = new URL(location.href).searchParams.get('fixture')
        const callbacks = new Map()
        const listeners = new Map()
        let nextID = 0
        const mock = window.__desktopFixture = {
            commands: [], dirty: false, exited: false, failProxy: false, conflict: false, reloadFails: false, canInstall: true,
            updateAvailable: fixtureCase !== 'update-none', updateError: fixtureCase === 'update-failure',
            holdUpdateCheck: fixtureCase === 'update-held', holdUpdateInstall: false,
            finishUpdateCheck: null, finishUpdateInstall: null,
            holdStart: fixtureCase === 'update-start-held', finishStart: null,
            holdStatus: fixtureCase === 'startup-status-held', finishStatus: null,
            statusReadError: fixtureCase === 'startup-status-failed',
            holdConfigSave: fixtureCase === 'update-write-held', finishConfigSave: null,
            file: { path: 'C:\\Users\\Fixture\\AppData\\Local\\mihomo\\config.yaml', content: 'mode: rule\nproxies: []\n', revision: 'revision-1', max_size: 4194304, writable: true },
            status: {
                appVersion: '0.1.0', phase: ['update-starting', 'startup-status-held', 'startup-status-failed'].includes(fixtureCase) ? 'starting' : 'stopped', systemProxy: false, tun: false, autostart: false,
                controller: null, lastError: null, coreVersion: null,
                configPath: 'C:\\Users\\Fixture\\AppData\\Local\\mihomo\\config.yaml', logPath: 'C:\\Users\\Fixture\\AppData\\Local\\mihomo\\logs',
                service: { installed: false, running: false },
                capabilities: { systemProxy: true, autostart: true, tun: true, configImport: true, serviceManagement: true },
            },
            emit(name, payload) {
                for (const [id, handler] of listeners.get(name) || []) callbacks.get(handler)?.({ event: name, id, payload: structuredClone(payload) })
            },
            phase(phase) {
                mock.status.phase = phase
                mock.status.controller = phase === 'running' ? { url: origin, secret } : null
                mock.status.coreVersion = phase === 'running' ? 'fixture-core' : null
                mock.emit('desktop-status', mock.status)
            },
        }
        window.__TAURI_INTERNALS__ = {
            transformCallback(callback) { const id = ++nextID; callbacks.set(id, callback); return id },
            async invoke(command, args = {}) {
                if (command === 'plugin:event|listen') {
                    const id = ++nextID
                    if (!listeners.has(args.event)) listeners.set(args.event, new Map())
                    listeners.get(args.event).set(id, args.handler)
                    return id
                }
                if (command === 'plugin:event|unlisten') { listeners.get(args.event)?.delete(args.eventId); return }
                mock.commands.push({ command, args: structuredClone(args) })
                if (command === 'desktop_status') {
                    if (mock.holdStatus) await new Promise(resolve => { mock.finishStatus = resolve })
                    if (mock.statusReadError) throw { code: 'desktop_unavailable', message: 'Fixture native status failure' }
                    return structuredClone(mock.status)
                }
                if (command === 'desktop_read_config') return structuredClone(mock.file)
                if (command === 'desktop_set_dirty') { mock.dirty = args.dirty; return }
                if (['desktop_start', 'desktop_restart'].includes(command)) {
                    if (mock.holdStart) {
                        mock.phase('starting')
                        await new Promise(resolve => { mock.finishStart = resolve })
                    }
                    mock.phase('running')
                    return structuredClone(mock.status)
                }
                if (command === 'desktop_stop') { mock.status.systemProxy = false; mock.phase('stopped'); return structuredClone(mock.status) }
                if (command === 'desktop_set_system_proxy') {
                    if (mock.failProxy) throw { code: 'system_proxy_failed', message: 'Fixture proxy rejection' }
                    mock.status.systemProxy = args.enabled
                } else if (command === 'desktop_set_autostart') mock.status.autostart = args.enabled
                else if (command === 'desktop_set_tun') {
                    mock.status.tun = args.enabled
                    mock.status.service = { installed: true, running: args.enabled }
                } else if (command === 'desktop_install_service') mock.status.service.installed = true
                else if (command === 'desktop_uninstall_service') mock.status.service = { installed: false, running: false }
                else if (['desktop_save_config', 'desktop_import_config', 'desktop_import_config_url'].includes(command)) {
                    if (mock.holdConfigSave) await new Promise(resolve => { mock.finishConfigSave = resolve })
                    if (mock.conflict || args.expectedRevision !== mock.file.revision) throw { code: 'config_conflict', message: 'Configuration changed' }
                    mock.file.content = command === 'desktop_import_config_url' ? 'mode: direct\n# imported by native URL download\n' : args.content
                    mock.file.revision = 'revision-' + (parseInt(mock.file.revision.split('-')[1]) + 1)
                    return structuredClone(mock.file)
                } else if (command === 'desktop_reload_config') {
                    if (mock.reloadFails) throw { code: 'reload_invalid', message: 'Fixture reload rejection' }
                    return
                } else if (command === 'desktop_patch_config') return
                else if (['desktop_open_directory', 'desktop_open_external'].includes(command)) return
                else if (command === 'desktop_exit') {
                    if (mock.dirty && !args.force) mock.emit('desktop-exit-requested', {})
                    else mock.exited = true
                    return
                } else if (command === 'desktop_check_update') {
                    mock.emit('desktop-update-progress', { stage: 'checking' })
                    if (mock.holdUpdateCheck) await new Promise(resolve => { mock.finishUpdateCheck = resolve })
                    if (mock.updateError) {
                        mock.emit('desktop-update-progress', { stage: 'error', message: 'Fixture update network failure' })
                        throw { code: 'update_check_failed', message: 'Fixture update network failure' }
                    }
                    mock.emit('desktop-update-progress', { stage: 'ready' })
                    return { currentVersion: '0.1.0', available: mock.updateAvailable, canInstall: mock.canInstall,
                        ...(mock.updateAvailable ? { releaseUrl: 'https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.2.0', version: '0.2.0', body: 'Fixture release notes' } : {}),
                    }
                } else if (command === 'desktop_install_update') {
                    if (mock.dirty) throw { code: 'config_busy', message: 'Unsaved changes' }
                    mock.emit('desktop-update-progress', { stage: 'downloading', downloaded: 100, total: 200 })
                    if (mock.holdUpdateInstall) await new Promise(resolve => { mock.finishUpdateInstall = resolve })
                    mock.emit('desktop-update-progress', { stage: 'ready', downloaded: 200, total: 200 })
                    return
                } else throw { code: 'unknown_command', message: command }
                mock.emit('desktop-status', mock.status)
                return structuredClone(mock.status)
            },
        }
        window.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener(event, id) { listeners.get(event)?.delete(id) } }
    }
    await context.addInitScript(initializeFixture, { origin, secret })

    const page = await context.newPage()
    page.on('pageerror', error => pageErrors.push(error.message))
    await page.route('**/*', route => {
        if (route.request().url().startsWith(origin)) return route.continue()
        pageErrors.push('Unexpected external request: ' + route.request().url())
        return route.abort()
    })
    await page.goto(origin + '/#/settings?host=untrusted.invalid&port=4567&secret=untrusted-url')
    await page.getByRole('heading', { name: '本机运行', exact: true }).waitFor()
    await until(() => page.getByRole('button', { name: '启动内核', exact: true }).isEnabled(), 'native startup status enables start')
    assert.equal(await page.getByRole('button', { name: '启动内核', exact: true }).count(), 1, 'the status bar owns the single start control')
    assert.equal(await page.locator('.desktop-controls').getByRole('button', { name: /^(启动内核|停止内核)$/ }).count(), 0, 'Settings does not duplicate start and stop')
    assert.equal(await page.getByRole('button', { name: '编辑配置文件', exact: true }).count(), 1, 'Settings has one source-file editor entry')
    assert.equal(await page.getByRole('heading', { name: '控制器', exact: true }).count(), 0, 'desktop hides the browser controller card')
    assert.equal(await page.locator('.desktop-statusbar').getByRole('button', { name: '打开设置', exact: true }).count(), 0, 'the Settings page does not link to itself in the status bar')
    assert.equal(await page.getByRole('button', { name: '检查并更新资源', exact: true }).isEnabled(), false, 'resource updates remain visible but disabled while stopped')
    await page.getByText('启动内核后可检查并更新规则与 Geo 数据。', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: '检查应用更新', exact: true }).isEnabled(), true, 'application updates do not require a running core')
    assert.equal(requests.length, 0, 'stopped core does not contact browser or URL controllers')
    assert.equal(await page.getByLabel('控制器密钥').count(), 0)
    assert.equal(await page.locator('link[rel="manifest"]').count(), 0, 'desktop has no PWA manifest')
    assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistrations()).length), 0, 'desktop has no service worker')

    const beforeRefresh = await page.evaluate(() => window.__desktopFixture.commands.length)
    await page.getByRole('button', { name: '刷新状态', exact: true }).click()
    await until(() => page.evaluate(start => window.__desktopFixture.commands.slice(start).some(item => item.command === 'desktop_status'), beforeRefresh), 'refresh reads native status')
    assert.deepEqual(await page.evaluate(start => window.__desktopFixture.commands.slice(start).filter(item => ['desktop_start', 'desktop_stop', 'desktop_restart', 'desktop_reload_config'].includes(item.command)), beforeRefresh), [], 'refresh does not mutate the runtime')
    await page.getByRole('button', { name: '重新加载已保存配置', exact: true }).click()
    await page.getByText('配置校验完成，将在下次启动内核时生效。', { exact: true }).waitFor()
    assert.equal(await page.evaluate(() => window.__desktopFixture.status.phase), 'stopped', 'reloading a stopped core does not start it')
    assert.equal(await page.evaluate(() => window.__desktopFixture.commands.filter(item => item.command === 'desktop_reload_config').length), 1, 'the moved reload entry submits one native reload')

    await page.getByRole('button', { name: 'English', exact: true }).click()
    await page.getByText('Start the core to check and update rules and Geo data.', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: 'Reload saved configuration', exact: true }).count(), 1, 'the English desktop controls use the same single configuration entry')
    assert.equal(await page.getByRole('button', { name: 'Check and update resources', exact: true }).isEnabled(), false)
    await page.getByRole('button', { name: '中文', exact: true }).click()

    await page.getByRole('button', { name: '编辑配置文件', exact: true }).click()
    const editor = page.locator('textarea.config-editor-text')
    await until(() => editor.isEditable(), 'native config editing is available while core is stopped')
    await editor.fill('mode: direct\nproxies: []\n# retained draft\n')
    await until(() => page.evaluate(() => window.__desktopFixture.dirty), 'editing marks the native exit guard dirty')
    await page.evaluate(() => { location.hash = '#/connections' })
    assert.ok((await editor.inputValue()).includes('retained draft'), 'desktop route navigation does not discard the source-file draft')
    await page.evaluate(() => { location.hash = '#/settings' })
    await page.evaluate(() => window.__desktopFixture.phase('running'))
    await until(() => requests.some(item => item.path === '/version'), 'running controller receives the native credential')
    assert.ok((await editor.inputValue()).includes('retained draft'), 'the editor survives a controller startup')
    await page.evaluate(() => window.__desktopFixture.phase('error'))
    assert.ok((await editor.inputValue()).includes('retained draft'), 'the editor survives a core failure')
    await page.evaluate(() => window.__desktopFixture.emit('desktop-exit-requested', {}))
    await page.getByRole('alertdialog', { name: '退出前还有未保存的修改' }).waitFor()
    await page.getByRole('button', { name: '继续编辑', exact: true }).last().click()
    assert.equal(await page.evaluate(() => window.__desktopFixture.exited), false)
    await page.evaluate(() => { window.__desktopFixture.conflict = true })
    await page.locator('.config-editor-footer').getByRole('button', { name: '保存并重新加载' }).click()
    await page.getByText('配置文件已被其他操作修改，当前草稿已保留。请先复制需要保留的内容，再重新读取文件进行合并。', { exact: true }).waitFor()
    assert.ok((await editor.inputValue()).includes('retained draft'), 'native revision conflicts preserve the draft')
    assert.equal(await page.evaluate(() => window.__desktopFixture.file.content), 'mode: rule\nproxies: []\n', 'a conflicted save does not replace the source')
    await page.locator('.config-editor-footer').getByRole('button', { name: '关闭', exact: true }).click()
    await page.getByRole('button', { name: '丢弃并关闭', exact: true }).click()
    await page.evaluate(() => { window.__desktopFixture.conflict = false; window.__desktopFixture.reloadFails = true })
    await page.getByRole('button', { name: '编辑配置文件', exact: true }).click()
    await until(() => editor.isEditable(), 'a new editor session reads the native revision')
    await editor.fill('mode: direct\nproxies: []\n# retry reload\n')
    await page.locator('.config-editor-footer').getByRole('button', { name: '保存并重新加载' }).click()
    await page.getByText('配置文件已保存，但未通过完整配置校验，重载未完成。请检查配置，修改后再次保存并重新加载。', { exact: true }).waitFor()
    assert.ok(await page.evaluate(() => window.__desktopFixture.file.content.includes('retry reload')), 'a failed reload keeps the successful save')
    await page.evaluate(() => { window.__desktopFixture.reloadFails = false })
    await page.getByRole('button', { name: '重试重新加载', exact: true }).click()
    await page.getByText('配置已保存，将在下次启动内核时生效。', { exact: true }).waitFor()
    await until(() => page.evaluate(() => !window.__desktopFixture.dirty), 'successful native save clears draft protection')
    await page.locator('.config-editor-footer').getByRole('button', { name: '关闭', exact: true }).click()
    assert.equal(requests.filter(item => item.path === '/configs/file').length, 0, 'configuration I/O always uses native source-file commands')

    await page.getByRole('button', { name: '启动内核', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.status.phase === 'running'), 'start invokes native host')
    assert.equal(await page.getByRole('button', { name: '停止内核', exact: true }).count(), 1, 'a running core has only the status-bar stop control')
    const beforeRestart = await page.evaluate(() => window.__desktopFixture.commands.length)
    await page.getByRole('button', { name: '重启内核', exact: true }).click()
    await until(() => page.evaluate(start => window.__desktopFixture.commands.slice(start).some(item => item.command === 'desktop_restart'), beforeRestart), 'restart remains a distinct native operation')
    assert.equal(await page.evaluate(start => window.__desktopFixture.commands.slice(start).filter(item => item.command === 'desktop_reload_config').length, beforeRestart), 0, 'restart is not replaced with configuration reload')
    const resources = page.locator('.settings-update[aria-label="规则与 Geo 数据"]')
    await resources.getByText('检查当前配置中的规则集与 Geo 数据来源，并应用可用的资源更新。', { exact: true }).waitFor()
    assert.equal(await resources.innerText().then(text => /内核最后升级|升级后会重启|内核升级/.test(text)), false, 'resource-only idle text never promises a core upgrade')
    await resources.getByRole('button', { name: '检查并更新资源', exact: true }).click()
    await resources.getByText('正在检查并应用规则与 Geo 数据更新。', { exact: true }).waitFor()
    assert.equal(requests.filter(item => item.path === '/upgrade/check' && item.method === 'POST').length, 1, 'resource updating keeps the existing core-owned task API')
    resourceTask.state = 'partial'
    Object.assign(resourceTask.items.find(item => item.kind === 'geodata'), { status: 'error', error: 'update_failed' })
    await resources.getByText('部分资源更新失败，请查看各项结果。', { exact: true }).waitFor()
    assert.equal(await resources.innerText().then(text => /内核最后升级|升级后会重启|内核升级/.test(text)), false, 'resource-only results stay scoped to resources')
    const proxySwitch = page.getByRole('switch', { name: '设置为系统代理', exact: true })
    await proxySwitch.click()
    await until(() => proxySwitch.getAttribute('aria-checked').then(value => value === 'true'), 'system proxy uses native confirmed state')
    await proxySwitch.click()
    await until(() => proxySwitch.getAttribute('aria-checked').then(value => value === 'false'), 'system proxy can be disabled')
    await page.evaluate(() => { window.__desktopFixture.failProxy = true })
    await proxySwitch.click()
    await page.getByText('Fixture proxy rejection', { exact: true }).first().waitFor()
    assert.equal(await proxySwitch.getAttribute('aria-checked'), 'false', 'failed OS operation never flips the switch optimistically')
    await page.evaluate(() => { window.__desktopFixture.failProxy = false })
    await page.getByRole('switch', { name: '开机时启动', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.status.autostart), 'Windows autostart is connected without ClashX emulation')
    await page.getByRole('switch', { name: 'TUN 模式', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.status.tun), 'TUN reaches native service path')
    await page.getByRole('button', { name: '停止内核', exact: true }).click()
    await until(() => page.locator('.desktop-status-details').innerText().then(text => text.includes('TUN 模式 关闭')), 'stopping the core clears effective TUN status')
    assert.equal(await page.getByRole('switch', { name: 'TUN 模式', exact: true }).getAttribute('aria-checked'), 'true', 'the selected TUN mode survives stopping')
    await page.getByRole('button', { name: '启动内核', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.status.phase === 'running'), 'restart applies the selected mode')

    await page.getByRole('button', { name: '导入配置', exact: true }).click()
    const importFile = page.getByLabel('选择文件', { exact: true })
    await importFile.setInputFiles({ name: 'first.yaml', mimeType: 'application/yaml', buffer: Buffer.from('mode: rule\n# first import file\n') })
    await until(() => page.getByLabel('配置内容', { exact: true }).inputValue().then(value => value.includes('first import file')), 'file selection loads a YAML preview')
    await page.evaluate(() => { location.hash = '#/connections' })
    assert.ok((await page.getByLabel('配置内容', { exact: true }).inputValue()).includes('first import file'), 'desktop route navigation preserves an import draft')
    await page.evaluate(() => { location.hash = '#/settings' })
    await importFile.setInputFiles({ name: 'oversized.yaml', mimeType: 'application/yaml', buffer: Buffer.alloc(4194305, 'a') })
    await page.getByText('配置文件超过 4 MiB 限制。', { exact: true }).waitFor()
    assert.equal(await page.getByLabel('配置内容', { exact: true }).inputValue(), '', 'an invalid new selection cannot accidentally import the previous file')
    await page.getByRole('button', { name: '配置 URL', exact: true }).click()
    await page.getByLabel('配置 URL', { exact: true }).fill('https://fixture.invalid/profile.yaml')
    await page.getByRole('button', { name: '替换并应用', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.file.content.includes('native URL')), 'configuration URL downloads via native command')
    await until(() => page.locator('.desktop-import-modal.modal-show').count().then(count => count === 0), 'successful import closes the dialog')
    await page.getByRole('button', { name: '检查应用更新', exact: true }).click()
    await page.getByRole('button', { name: '安装并重新启动', exact: true }).waitFor()
    await page.getByRole('button', { name: '安装并重新启动', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.commands.some(item => item.command === 'desktop_install_update')), 'app update is delegated to native verified installer')
    await page.evaluate(() => { window.__desktopFixture.canInstall = false })
    await page.getByRole('button', { name: '检查应用更新', exact: true }).click()
    await page.getByRole('button', { name: '下载完整安装包', exact: true }).click()
    await until(() => page.evaluate(() => window.__desktopFixture.commands.some(item => item.command === 'desktop_open_external' && item.args.url === 'https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.2.0')), 'portable updates open the native release URL')
    assert.equal(await page.evaluate(() => window.__desktopFixture.commands.filter(item => item.command === 'desktop_install_update').length), 1, 'a portable update never invokes the installed-app updater')
    assert.equal(await page.locator('[data-update-id="kernel"]').count(), 0, 'desktop resource updates cannot offer core self-upgrade')
    assert.equal(await page.locator('[data-update-id="ui"]').count(), 0, 'bundled UI is not overwritten by web dashboard updates')

    await page.locator('.sidebar-menu').getByRole('link', { name: '连接', exact: true }).click()
    assert.equal(await page.locator('.desktop-statusbar').getByRole('button', { name: '打开设置', exact: true }).count(), 1, 'other routes retain the Settings shortcut')
    await until(() => [...sockets].some(socket => socket.kind === '/connections' && socket.readyState === 1), 'connection statistics stream starts')
    await page.evaluate(() => window.__desktopFixture.emit('desktop-visibility', { visible: false }))
    assert.equal(await page.evaluate(() => document.visibilityState), 'visible')
    await until(() => sockets.size === 0, 'native tray hiding stops statistics and log WebSockets')
    await page.evaluate(() => window.__desktopFixture.emit('desktop-visibility', { visible: true }))
    await until(() => [...sockets].some(socket => socket.kind === '/connections' && socket.readyState === 1), 'native show reconnects statistics')
    assert.equal(await page.evaluate(secret => JSON.stringify(localStorage).includes(secret), secret), false, 'native credential is not persisted')
    assert.equal(await page.evaluate(() => window.__desktopFixture.commands.filter(item => item.command === 'desktop_open_external').length), 1, 'dashboard router links never add a system browser launch')
    await page.evaluate(() => {
        const link = document.createElement('a')
        link.href = 'https://fixture.invalid/help'
        document.body.append(link)
        link.click()
        link.remove()
    })
    await until(() => page.evaluate(() => window.__desktopFixture.commands.some(item => item.command === 'desktop_open_external' && item.args.url === 'https://fixture.invalid/help')), 'external links are handed to the native system browser')

    if (process.env.DESKTOP_SCREENSHOT) {
        await page.locator('.sidebar-menu').getByRole('link', { name: '设置', exact: true }).click()
        await until(() => page.locator('.message-container').count().then(count => count === 0), 'transient test notifications finish before the screenshot')
        await page.evaluate(async () => { await document.fonts.ready; document.querySelector('.page-container').scrollTop = 0 })
        await page.screenshot({ path: process.env.DESKTOP_SCREENSHOT, fullPage: true })
    }
    await context.close()
    await verifyStartupUpdates(origin, initializeFixture)
    assert.deepEqual(pageErrors, [], 'desktop runs without unhandled errors or remote requests')
    process.stdout.write('Desktop UI integration passed: controlled controller, offline editor/draft recovery, native settings/import/update, PWA isolation, tray pause/resume.\n')
}

main().catch(error => { console.error(error); process.exitCode = 1 }).finally(async () => {
    clearInterval(tick)
    for (const socket of sockets) socket.terminate()
    if (browser) await browser.close()
    if (server) await new Promise(resolve => { server.closeAllConnections(); server.close(resolve) })
})
