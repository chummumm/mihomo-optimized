const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { test } = require('node:test')
const ts = require('typescript')

const compiled = ts.transpileModule(fs.readFileSync(path.join(__dirname, 'model.ts'), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText
const loaded = { exports: {} }
new Function('module', 'exports', compiled)(loaded, loaded.exports)
const { DesktopError, desktopAPIInfo, desktopError, readDesktopStatus } = loaded.exports

function status(overrides = {}) {
    return {
        appVersion: '0.1.0', phase: 'running', systemProxy: false, tun: false, autostart: false,
        controller: { url: 'http://127.0.0.1:32189', secret: 'random-per-install-native-secret' },
        lastError: null, coreVersion: 'fixture', configPath: 'C:\\fixture\\config.yaml', logPath: 'C:\\fixture\\logs',
        service: { installed: false, running: false },
        capabilities: { systemProxy: true, autostart: true, tun: true, configImport: true, serviceManagement: true },
        ...overrides,
    }
}

test('desktop connects only the native-managed running loopback controller', () => {
    assert.deepEqual(desktopAPIInfo(readDesktopStatus(status())), {
        hostname: '127.0.0.1', port: '32189', protocol: 'http:', secret: 'random-per-install-native-secret', enabled: true,
    })
    for (const phase of ['stopped', 'starting', 'stopping', 'error']) {
        const connection = desktopAPIInfo(status({ phase }))
        assert.equal(connection.enabled, false)
        assert.equal(connection.secret, '', 'inactive controllers cannot leak a reusable credential')
    }
    assert.equal(desktopAPIInfo(null).enabled, false)
})

test('remote, credential-bearing and URL-overridable native controllers are rejected', () => {
    for (const url of [
        'https://127.0.0.1:32189', 'http://localhost:32189', 'http://192.0.2.5:32189',
        'http://127.0.0.1', 'http://user:pass@127.0.0.1:32189', 'http://127.0.0.1:32189/api',
        'http://127.0.0.1:32189/?secret=override', 'http://127.0.0.1:32189/#host=evil', 'not a URL',
    ]) {
        const value = status({ controller: { url, secret: 'fixture-secret' } })
        assert.equal(desktopAPIInfo(value).enabled, false, url)
        assert.throws(() => readDesktopStatus(value), DesktopError)
    }
    assert.throws(() => readDesktopStatus(status({ controller: { url: 'http://127.0.0.1:32189', secret: '' } })), DesktopError)
})

test('malformed native status cannot enable capabilities or falsely show running', () => {
    for (const value of [null, {}, [], status({ phase: 'ready' }), status({ systemProxy: 'true' }),
        status({ capabilities: {} }), status({ service: { installed: true } }), status({ controller: undefined }),
        status({ lastError: {} }), status({ appVersion: 1 })]) {
        assert.throws(() => readDesktopStatus(value), DesktopError)
    }
    assert.equal(readDesktopStatus(status({ phase: 'stopped', controller: null })).phase, 'stopped')
})

test('only structured host errors are shown; arbitrary exception text stays private', () => {
    const safe = desktopError({ code: 'config_conflict', message: 'Configuration changed' })
    assert.equal(safe.code, 'config_conflict')
    assert.equal(safe.message, 'Configuration changed')
    for (const value of ['secret: should-not-be-shown', new Error('proxy-password: hidden'), { message: 'sensitive config' }]) {
        const error = desktopError(value)
        assert.equal(error.code, 'desktop_unavailable')
        assert.equal(error.message, 'Desktop connection unavailable')
    }
})
