const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const { createRequire } = require('node:module')
const { test } = require('node:test')
const ts = require('typescript')

const loadedSources = new Map()
function loadSource(relative) {
    const filename = path.resolve(__dirname, relative)
    if (loadedSources.has(filename)) return loadedSources.get(filename).exports
    const compiled = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
        compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, esModuleInterop: true },
    }).outputText
    const module = { exports: {} }
    loadedSources.set(filename, module)
    const requireModule = createRequire(filename)
    const localRequire = name => {
        const local = path.resolve(path.dirname(filename), name + '.ts')
        return name.startsWith('.') && fs.existsSync(local) ? loadSource(local) : requireModule(name)
    }
    new Function('require', 'module', 'exports', compiled)(localRequire, module, module.exports)
    return module.exports
}

const { ThroughputMotion } = loadSource('chart-motion.ts')
const { RATE_STALE_MS } = loadSource('throughput-history.ts')
const MiB = 1024 * 1024

function sample(at, down, up = 0, segment = 1) {
    return Object.freeze({ at, down, up, segment, span: 1000 })
}

test('tail eases for 250 ms without changing measurements or their timestamps', () => {
    const motion = new ThroughputMotion()
    const history = [sample(0, 100, 40)]
    motion.frame(history, 0, false)
    history.push(sample(1000, 900, 120))
    Object.freeze(history)
    const original = structuredClone(history)

    assert.deepEqual(motion.frame(history, 1000, false).head, { at: 1000, down: 100, up: 40 })
    const middle = motion.frame(history, 1125, false).head
    assert(middle.down > 100 && middle.down < 900)
    assert(middle.up > 40 && middle.up < 120)
    assert.equal(middle.at, 1000, 'animation must not invent a new sample time')
    assert.deepEqual(motion.frame(history, 1250, false).head, { at: 1000, down: 900, up: 120 })
    assert.deepEqual(history, original, 'the chart and tooltip retain the real measurements')
})

test('a new target redirects from the current visual value without replaying old targets', () => {
    const motion = new ThroughputMotion()
    const history = [sample(0, 100)]
    motion.frame(history, 0, false)
    history.push(sample(1000, 1100))
    motion.frame(history, 1000, false)
    const before = motion.frame(history, 1100, false).head.down
    // The rate history replaces its active second bucket on a fast update.
    history[1] = sample(1100, 300)
    assert.equal(motion.frame(history, 1100, false).head.down, before)
    const redirected = motion.frame(history, 1225, false).head.down
    assert(redirected > 300 && redirected < before)
    assert.equal(motion.frame(history, 1350, false).head.down, 300)
    assert.equal(motion.frame(history, 1500, false).head.down, 300)
})

test('new measurement periods, replaced history and an explicit resume reset snap immediately', () => {
    for (const boundary of ['segment', 'history', 'reset', 'clock', 'long-pause']) {
        const motion = new ThroughputMotion()
        let history = [sample(0, 100)]
        motion.frame(history, 0, false)
        history.push(sample(1000, 900))
        motion.frame(history, 1000, false)
        motion.frame(history, 1100, false)
        let now = 1150
        if (boundary === 'segment') history.push(sample(now, 300, 0, 2))
        if (boundary === 'history') history = [sample(now, 300)]
        if (boundary === 'reset') {
            motion.reset()
            history.push(sample(now, 300))
        }
        if (boundary === 'clock') history[1] = sample(500, 300)
        if (boundary === 'long-pause') {
            now += RATE_STALE_MS + 1
            history.push(sample(now, 300))
        }
        const result = motion.frame(history, now, false)
        assert.equal(result.head.down, 300, boundary)
        assert.equal(result.head.at, history.at(-1).at, boundary)
    }
})

test('a stopped stream ages its real head and never fills missing time with animated data', () => {
    const motion = new ThroughputMotion()
    const history = [sample(1000, 2048)]
    motion.frame(history, 1000, false)
    assert.deepEqual(motion.frame(history, 1500, false).head, { at: 1000, down: 2048, up: 0 })
    assert.equal(motion.frame(history, 1000 + RATE_STALE_MS, false).head.at, 1000)
    assert.equal(motion.frame(history, 1001 + RATE_STALE_MS, false).head, undefined)
    history.push(sample(1002 + RATE_STALE_MS, 4096))
    assert.equal(motion.frame(history, 1002 + RATE_STALE_MS, false).head.down, 4096)
    assert.equal(history.length, 2)
})

test('empty history and reduced motion abandon an unfinished tail transition', () => {
    const motion = new ThroughputMotion()
    const history = [sample(0, 100)]
    motion.frame(history, 0, false)
    history.push(sample(1000, 900))
    motion.frame(history, 1000, false)
    assert.equal(motion.frame(history, 1100, true).head.down, 900)
    history.length = 0
    const empty = motion.frame(history, 1150, false)
    assert.equal(empty.head, undefined)
    assert(empty.ymax > 0)
    history.push(sample(1200, 300))
    assert.equal(motion.frame(history, 1200, false).head.down, 300)
})

test('the scale immediately contains a new real peak and also contains a falling visual tail', () => {
    const motion = new ThroughputMotion()
    const history = [sample(0, 2048)]
    motion.frame(history, 0, false)
    history.push(sample(1000, 100 * MiB))
    const peak = motion.frame(history, 1000, false)
    assert(peak.ymax >= 100 * MiB, 'a new measured peak must not be clipped during animation')
    motion.frame(history, 1250, false)
    history.splice(0, history.length, sample(2000, 2048))
    const falling = motion.frame(history, 2000, false)
    assert.equal(falling.head.down, 100 * MiB)
    assert(falling.ymax >= falling.head.down)
    const middle = motion.frame(history, 2125, false)
    assert(middle.ymax >= middle.head.down)
    assert.equal(motion.frame(history, 2250, false).head.down, 2048)
})

test('scale contraction depends on elapsed time rather than the number of drawn frames', () => {
    function run(steps) {
        const motion = new ThroughputMotion()
        const history = [sample(0, MiB), sample(500, 1024)]
        const initial = motion.frame(history, 500, false).ymax
        history.shift()
        let result
        for (let i = 1; i <= steps; i++) result = motion.frame(history, 500 + i * 1000 / steps, false)
        assert(result.ymax < initial && result.ymax > 1024 * 1.2)
        return result.ymax
    }
    const slow = run(10)
    const fast = run(60)
    assert(Math.abs(slow - fast) < 1e-6)
})
