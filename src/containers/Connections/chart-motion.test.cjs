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
const {
    ThroughputHistory, rateHistoryState, pruneRateSamples, rateSampleX,
    RATE_DRAW_DELAY_MS, RATE_CLOCK_MARGIN_MS, RATE_SAMPLE_LIMIT, RATE_STALE_MS, RATE_WINDOW_MS,
} = loadSource('throughput-history.ts')

function sample(at, down, up = 0, segment = 1) {
    return { at, down, up, segment, span: 1000 }
}

function close(actual, expected, message, tolerance = 1e-5) {
    assert(Math.abs(actual - expected) < tolerance, message + ': ' + actual + ' vs ' + expected)
}

function valueAt(frame, at, key = 'down') {
    const curve = frame.paths.flatMap(path => path.curves).find(curve => curve.from.at <= at && curve.to.at >= at)
    assert(curve, 'a visible historical position still has a curve')
    let low = 0, high = 1
    for (let i = 0; i < 40; i++) {
        const t = (low + high) / 2
        const x = (1 - t) ** 2 * curve.from.at + 2 * (1 - t) * t * curve.control.at + t ** 2 * curve.to.at
        if (x < at) low = t; else high = t
    }
    const t = (low + high) / 2
    return (1 - t) ** 2 * curve.from[key] + 2 * (1 - t) * t * curve.control[key] + t ** 2 * curve.to[key]
}

function populated(until = 70000) {
    const history = new ThroughputHistory()
    for (let at = 0; at <= until; at += 1000) {
        history.feed({ uploadTotal: at * 2, downloadTotal: at * 10 }, at)
    }
    return history
}

test('the buffered head follows the original quadratic and never rewrites measured rates', () => {
    const history = new ThroughputHistory()
    let total = 0
    history.feed({ uploadTotal: 0, downloadTotal: total }, 0)
    for (let second = 1; second <= 10; second++) {
        total += second * 100
        assert.equal(history.feed({ uploadTotal: 0, downloadTotal: total }, second * 1000).down, second * 100)
    }
    const original = structuredClone(history.samples)
    const frame = new ThroughputMotion().frame(history.samples, 10500, true)
    assert.equal(frame.endAt, 10500 - RATE_DRAW_DELAY_MS)
    close(frame.head.at, frame.endAt, 'the stable curve reaches the right boundary')
    close(frame.head.down, 750, 'the old midpoint curve keeps its original geometry')
    assert(frame.samples.every(sample => sample.at <= frame.endAt))
    assert(!frame.samples.includes(rateHistoryState(history.samples).pending))
    assert.deepEqual(history.samples, original, 'drawing never changes measurements or their sample times')
})

test('one-second sampling with up to 500 ms delivery jitter keeps a fixed right edge', () => {
    const history = new ThroughputHistory()
    const motion = new ThroughputMotion()
    let sourceAt = 0
    for (let now = 0; now <= 14000; now += 25) {
        while (sourceAt + 500 <= now) {
            history.feed({ uploadTotal: sourceAt, downloadTotal: sourceAt * 3 }, sourceAt)
            sourceAt += 1000
        }
        const frame = motion.frame(history.samples, now, false)
        if (now >= 6000) close(rateSampleX(frame.head.at, frame.endAt, 1080), 1080, 'the head never resets one sample to the right')
    }
})

test('left guard eviction preserves the visible quadratic instead of moving its start inward', () => {
    const history = Array.from({ length: 101 }, (_, i) => sample(i * 1000, 200 + (i % 7) * 100))
    const motion = new ThroughputMotion()
    const before = motion.frame(history, 103000, true)
    pruneRateSamples(history, 103001)
    assert(history.length < 70)
    const after = motion.frame(history, 103001, true)
    close(after.paths[0].curves[0].from.at, after.endAt - RATE_WINDOW_MS, 'the curve is clipped exactly at the left edge')
    for (const at of [40050, 40200, 40700, 41200]) close(valueAt(before, at), valueAt(after, at), 'guard removal leaves old waveform geometry unchanged')
})

test('offscreen controls and future buffered peaks do not enter tooltips or inflate the scale', () => {
    const history = Array.from({ length: 68 }, (_, i) => sample((i - 64) * 1000, i < 2 || i > 65 ? 1e9 : 100))
    const frame = new ThroughputMotion().frame(history, RATE_DRAW_DELAY_MS, true)
    assert(frame.samples.every(sample => sample.at >= -RATE_WINDOW_MS && sample.at <= 0))
    assert(frame.samples.every(sample => sample.down === 100))
    assert.equal(frame.ymax, 1024 * 1.2)
})

test('late updates to the same weighted bucket cannot reshape already displayed curves', () => {
    const history = new ThroughputHistory()
    history.feed({ uploadTotal: 0, downloadTotal: 0 }, 8000)
    history.feed({ uploadTotal: 90, downloadTotal: 90 }, 8900)
    history.feed({ uploadTotal: 190, downloadTotal: 190 }, 9900)
    history.feed({ uploadTotal: 210, downloadTotal: 210 }, 10100)
    const motion = new ThroughputMotion()
    const before = motion.frame(history.samples, 13400, true)
    close(before.head.at, 9400, 'an incomplete future bucket cannot support the head')
    history.feed({ uploadTotal: 900, downloadTotal: 1500 }, 10900)
    const after = motion.frame(history.samples, 13400, true)
    assert.deepEqual(after.paths, before.paths, 'changing a delayed active bucket leaves committed geometry intact')
    assert.equal(rateHistoryState(history.samples).pending, history.samples.at(-1))
    history.feed({ uploadTotal: 920, downloadTotal: 1520 }, 11100)
    const committed = motion.frame(history.samples, 13400, true)
    close(valueAt(committed, 9200), valueAt(before, 9200), 'a new stable curve never redraws the old one')
    assert(committed.head.at > after.head.at)
})

test('separate measurement periods stay separate and hide the old live marker', () => {
    const history = [sample(0, 100), sample(1000, 200), sample(2000, 100),
        sample(8000, 500, 0, 2), sample(9000, 600, 0, 2), sample(10000, 500, 0, 2)]
    const motion = new ThroughputMotion()
    const gap = motion.frame(history, 10000, true)
    assert.equal(gap.head, undefined, 'an earlier segment is never represented as the current stream')
    const frame = motion.frame(history, 14000, true)
    assert.equal(frame.paths.length, 2)
    assert(frame.paths[0].curves.at(-1).to.at < frame.paths[1].curves[0].from.at)
    assert(frame.paths.every(path => path.curves.every(curve => curve.to.at - curve.from.at <= 1000)))
})

test('a stopped stream leaves missing time blank and eventually has no drawing or timers', () => {
    const history = populated(10000)
    const motion = new ThroughputMotion()
    const stopped = motion.frame(history.samples, 16000, true)
    assert(stopped.head.at < stopped.endAt)
    assert(stopped.paths.length > 0)
    const stale = motion.frame(history.samples, 10001 + RATE_STALE_MS, true)
    assert.equal(stale.head, undefined)
    const expiredAt = 10001 + RATE_WINDOW_MS + RATE_DRAW_DELAY_MS + RATE_CLOCK_MARGIN_MS
    pruneRateSamples(history.samples, expiredAt)
    const expired = motion.frame(history.samples, expiredAt, true)
    assert.equal(history.samples.length, 0)
    assert.equal(expired.paths.length, 0)
    assert.equal(expired.active, false)
    assert.equal(expired.nextChangeAt, undefined)
})

test('clock corrections preserve x continuity while rates and sample intervals stay real', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    const before = motion.frame(history.samples, 70000, false)
    const observed = history.samples.find(sample => sample.at === 55000)
    const x = rateSampleX(observed.at, before.endAt, 600)
    const rates = history.samples.map(sample => [sample.up, sample.down, sample.span])
    history.shiftTime(-500)
    const after = motion.frame(history.samples, 70040, false)
    close(after.endAt, 70040 - RATE_DRAW_DELAY_MS - 490, 'only ten milliseconds of calibration are absorbed in a 40 ms frame')
    close(rateSampleX(observed.at, after.endAt, 600) - x, -0.5, 'all historical points move continuously during clock improvement')
    assert.deepEqual(history.samples.map(sample => [sample.up, sample.down, sample.span]), rates)
    const settled = motion.frame(history.samples, 73000, false)
    assert.equal(settled.endAt, 73000 - RATE_DRAW_DELAY_MS)
})

test('small negative clock correction and simultaneous pruning retain a full left edge', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    motion.frame(history.samples, 70000, false)
    history.shiftTime(-900)
    pruneRateSamples(history.samples, 70040)
    const frame = motion.frame(history.samples, 70040, false)
    close(frame.paths[0].curves[0].from.at, frame.endAt - RATE_WINDOW_MS, 'phase compensation has enough left controls')
    assert(history.samples.length <= RATE_SAMPLE_LIMIT)
    assert.equal(frame.samples[0].down, 10000)
})

test('large clock changes and resume reset immediately instead of replaying a long backlog', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    motion.frame(history.samples, 70000, false)
    history.shiftTime(-60000)
    const corrected = motion.frame(history.samples, 70040, false)
    assert.equal(corrected.endAt, 70040 - RATE_DRAW_DELAY_MS)
    history.shiftTime(-500)
    motion.frame(history.samples, 70080, false)
    motion.reset()
    const resumed = motion.frame(history.samples, 70120, false)
    assert.equal(resumed.endAt, 70120 - RATE_DRAW_DELAY_MS)
    assert.equal(resumed.head, undefined)
})

test('reduced-motion retirement always schedules a future deadline and finishes', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    let now = 71000
    let frames = 0
    while (true) {
        pruneRateSamples(history.samples, now)
        const frame = motion.frame(history.samples, now, true)
        if (frame.nextChangeAt === undefined) break
        assert(frame.nextChangeAt > now, 'retained guard points cannot schedule an expired deadline')
        now = frame.nextChangeAt + 1
        assert(++frames < 160, 'retirement is bounded, not a 1 ms timer loop')
    }
    assert.equal(history.samples.length, 0)
    assert(now < 70000 + RATE_WINDOW_MS + RATE_DRAW_DELAY_MS + RATE_CLOCK_MARGIN_MS + 10)
})

test('the 0.4.10 scale approaches a new peak gradually in both directions', () => {
    const motion = new ThroughputMotion()
    const history = [sample(0, 100), sample(1000, 100), sample(2000, 100)]
    let frame
    for (let i = 0; i < 120; i++) frame = motion.frame(history, 4000, false)
    const baseline = frame.ymax
    history.push(sample(3000, 1e8), sample(4000, 1e8), sample(5000, 1e8))
    const rising = motion.frame(history, 6000, false)
    close(rising.ymax, baseline + (1.2e8 - baseline) * 0.06, 'a peak no longer expands the axis instantly')
    assert(rising.ymax < 1.2e8)
    history.splice(0, history.length, sample(0, 100), sample(1000, 100), sample(2000, 100))
    const falling = motion.frame(history, 6000, false)
    close(falling.ymax, rising.ymax + (1024 * 1.2 - rising.ymax) * 0.06, 'the old contraction easing is restored')
    close(motion.frame(history, 6000, true).ymax, 1024 * 1.2, 'reduced motion applies the target scale directly')
})
