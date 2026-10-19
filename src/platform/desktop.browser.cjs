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

async function main() {
    assert.ok(fs.existsSync(path.join(dist, 'index.html')), 'build the desktop UI first')
    const ws = new WebSocketServer({ noServer: true })
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
            '/upgrade/check': { instance_id: 'fixture-instance', state: 'idle', items: [
                { id: 'kernel', kind: 'kernel', name: 'kernel', status: 'skipped', reason: 'desktop_managed' },
                { id: 'ui', kind: 'ui', name: 'ui', status: 'skipped', reason: 'desktop_managed' },
                { id: 'geodata', kind: 'geodata', name: 'GeoIP', status: 'up_to_date' },
            ] },
        }
        if (api[url.pathname]) {
            requests.push({ path: url.pathname, auth: req.headers.authorization })
            assert.equal(req.headers.authorization, `Bearer ${secret}`, 'HTTP uses only the native secret')
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
    await context.addInitScript(({ origin, secret }) => {
        localStorage.setItem('language', JSON.stringify('zh_CN'))
        localStorage.setItem('externalControllers', JSON.stringify([{ hostname: 'untrusted.invalid', port: 9999, secret: 'untrusted-storage' }]))
        const callbacks = new Map()
        const listeners = new Map()
        let nextID = 0
        const mock = window.__desktopFixture = {
            commands: [], dirty: false, exited: false, failProxy: false, conflict: false, reloadFails: false, canInstall: true,
            file: { path: 'C:\\Users\\Fixture\\AppData\\Local\\mihomo\\config.yaml', content: 'mode: rule\nproxies: []\n', revision: 'revision-1', max_size: 4194304, writable: true },
            status: {
                appVersion: '0.1.0', phase: 'stopped', systemProxy: false, tun: false, autostart: false,
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
                if (command === 'desktop_status') return structuredClone(mock.status)
                if (command === 'desktop_read_config') return structuredClone(mock.file)
                if (command === 'desktop_set_dirty') { mock.dirty = args.dirty; return }
                if (['desktop_start', 'desktop_restart'].includes(command)) { mock.phase('running'); return structuredClone(mock.status) }
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
                } else if (command === 'desktop_check_update') return { currentVersion: '0.1.0', available: true, canInstall: mock.canInstall, releaseUrl: 'https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.2.0', version: '0.2.0', body: 'Fixture release notes' }
                else if (command === 'desktop_install_update') {
                    if (mock.dirty) throw { code: 'config_busy', message: 'Unsaved changes' }
                    mock.emit('desktop-update-progress', { stage: 'downloading', downloaded: 100, total: 200 })
                    mock.emit('desktop-update-progress', { stage: 'ready', downloaded: 200, total: 200 })
                    return
                } else throw { code: 'unknown_command', message: command }
                mock.emit('desktop-status', mock.status)
                return structuredClone(mock.status)
            },
        }
        window.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener(event, id) { listeners.get(event)?.delete(id) } }
    }, { origin, secret })

    const page = await context.newPage()
    page.on('pageerror', error => pageErrors.push(error.message))
    await page.route('**/*', route => {
        if (route.request().url().startsWith(origin)) return route.continue()
        pageErrors.push('Unexpected external request: ' + route.request().url())
        return route.abort()
    })
    await page.goto(origin + '/#/settings?host=untrusted.invalid&port=4567&secret=untrusted-url')
    await page.getByRole('heading', { name: '本机运行', exact: true }).waitFor()
    await until(() => page.getByRole('button', { name: '启动内核', exact: true }).first().isEnabled(), 'native startup status enables start')
    assert.equal(requests.length, 0, 'stopped core does not contact browser or URL controllers')
    assert.equal(await page.getByLabel('控制器密钥').count(), 0)
    assert.equal(await page.locator('link[rel="manifest"]').count(), 0, 'desktop has no PWA manifest')
    assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistrations()).length), 0, 'desktop has no service worker')

    await page.getByRole('button', { name: '编辑配置文件', exact: true }).first().click()
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
    await page.getByRole('button', { name: '编辑配置文件', exact: true }).first().click()
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

    await page.getByRole('button', { name: '启动内核', exact: true }).first().click()
    await until(() => page.evaluate(() => window.__desktopFixture.status.phase === 'running'), 'start invokes native host')
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
    await page.getByRole('button', { name: '停止内核', exact: true }).first().click()
    await until(() => page.locator('.desktop-status-details').innerText().then(text => text.includes('TUN 模式 关闭')), 'stopping the core clears effective TUN status')
    assert.equal(await page.getByRole('switch', { name: 'TUN 模式', exact: true }).getAttribute('aria-checked'), 'true', 'the selected TUN mode survives stopping')
    await page.getByRole('button', { name: '启动内核', exact: true }).first().click()
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
    assert.deepEqual(pageErrors, [], 'desktop runs without unhandled errors or remote requests')
    process.stdout.write('Desktop UI integration passed: controlled controller, offline editor/draft recovery, native settings/import/update, PWA isolation, tray pause/resume.\n')
}

main().catch(error => { console.error(error); process.exitCode = 1 }).finally(async () => {
    clearInterval(tick)
    for (const socket of sockets) socket.terminate()
    if (browser) await browser.close()
    if (server) await new Promise(resolve => { server.closeAllConnections(); server.close(resolve) })
})
