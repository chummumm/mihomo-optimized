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
    RATE_CLOCK_MARGIN_MS, RATE_SAMPLE_LIMIT, RATE_STALE_MS, RATE_WINDOW_MS,
} = loadSource('throughput-history.ts')

function sample(at, down, up = 0, segment = 1) {
    return { at, down, up, segment, span: 1000 }
}

function close(actual, expected, message, tolerance = 1e-5) {
    assert(Math.abs(actual - expected) < tolerance, message + ': ' + actual + ' vs ' + expected)
}

function valueAt(frame, at, key = 'down', project = value => value) {
    const curve = frame.paths.flatMap(path => path.curves).find(curve => curve.from.at <= at && curve.to.at >= at)
    assert(curve, 'a visible historical position still has a curve')
    let low = 0, high = 1
    for (let i = 0; i < 40; i++) {
        const t = (low + high) / 2
        const x = (1 - t) ** 2 * curve.from.at + 2 * (1 - t) * t * curve.control.at + t ** 2 * curve.to.at
        if (x < at) low = t; else high = t
    }
    const t = (low + high) / 2
    return (1 - t) ** 2 * project(curve.from[key]) + 2 * (1 - t) * t * project(curve.control[key]) + t ** 2 * project(curve.to[key])
}

function screenHeight(frame, point, key = 'down') {
    return Math.min(point[key], frame.ymax) / frame.ymax
}

function screenHeightAt(frame, at, key = 'down') {
    return valueAt(frame, at, key, value => Math.min(value, frame.ymax) / frame.ymax)
}

function assertJoined(frame) {
    for (const path of frame.paths) {
        for (let index = 1; index < path.curves.length; index++) {
            const previous = path.curves[index - 1].to
            const current = path.curves[index].from
            close(previous.at, current.at, 'adjacent curves share one time position')
            for (const key of ['up', 'down']) close(screenHeight(frame, previous, key), screenHeight(frame, current, key), 'adjacent curves share one displayed height')
        }
    }
    if (!frame.tail) return
    const current = frame.paths.find(path => path.segment === frame.tail.segment)
    for (const key of ['up', 'down']) {
        close(screenHeight(frame, frame.tail.from, key), screenHeight(frame, frame.head, key), 'held tail starts at the displayed endpoint')
        close(screenHeight(frame, frame.tail.to, key), screenHeight(frame, frame.head, key), 'held tail and head end at the same height')
        if (current) close(screenHeight(frame, current.curves.at(-1).to, key), screenHeight(frame, frame.tail.from, key), 'the real curve and held tail meet without a vertical connector')
    }
}

const MiB = 1024 * 1024

function liveFixture(initialRate = 0.5 * MiB, peakRate = 4 * MiB) {
    const history = new ThroughputHistory()
    const motion = new ThroughputMotion()
    let sampledAt = 0
    let uploadTotal = 0
    let downloadTotal = 0
    history.feed({ uploadTotal, downloadTotal }, sampledAt)
    const feed = (at, down) => {
        const seconds = (at - sampledAt) / 1000
        uploadTotal += 0.5 * MiB * seconds
        downloadTotal += down * seconds
        sampledAt = at
        return history.feed({ uploadTotal, downloadTotal }, at)
    }
    feed(1000, peakRate)
    for (let at = 2000; at <= 10000; at += 1000) feed(at, initialRate)
    // A retained older peak fixes the axis independently of the live endpoint.
    // Reduced motion initializes that existing axis without a warm-up animation.
    motion.frame(history.samples, 10000, true)
    const frame = now => motion.frame(history.samples, now, false)
    return { history, motion, feed, frame }
}

function populated(until = 70000) {
    const history = new ThroughputHistory()
    for (let at = 0; at <= until; at += 1000) {
        history.feed({ uploadTotal: at * 2, downloadTotal: at * 10 }, at)
    }
    return history
}

test('the very first measured interval appears immediately without a drawing buffer', () => {
    const history = new ThroughputHistory()
    history.feed({ uploadTotal: 0, downloadTotal: 0 }, 0)
    const measured = history.feed({ uploadTotal: 100, downloadTotal: 700 }, 1000)
    const frame = new ThroughputMotion().frame(history.samples, 1000, false)
    assert.equal(frame.endAt, 1000)
    assert.deepEqual(frame.head, measured)
    assert.equal(frame.paths.length, 0, 'one sample needs no fabricated historical curve')
    assert.equal(frame.tail.segment, history.samples[0].segment)
    assert.deepEqual(frame.tail.from, measured)
    assert.equal(frame.tail.opacity, 1)
    assert.equal(frame.samples[0], history.samples[0], 'tooltip records are actual observations')
    assert.equal(history.samples.length, 1)
})

test('a new measurement is immediately available while only its displayed endpoint transitions', () => {
    const { history, feed, frame: draw } = liveFixture()
    const before = draw(11000)
    const rate = feed(11000, 2.5 * MiB)
    const original = structuredClone(history.samples)
    const frame = draw(11000)
    assert.equal(frame.endAt, 11000)
    close(screenHeight(frame, frame.head), screenHeight(before, before.head), 'a zero-time data change keeps the displayed height continuous')
    assert.equal(frame.samples.at(-1), history.samples.at(-1))
    assert.equal(frame.samples.at(-1).down, rate.down, 'tooltip samples contain the new real value immediately')
    assert.equal(frame.samples.at(-1).at, 11000, 'the sample timestamp has no drawing buffer')
    close(frame.paths.at(-1).curves.at(-1).to.at, 11000, 'the measured path includes the latest point immediately')
    assertJoined(frame)
    const middle = draw(11080)
    assert(screenHeight(middle, middle.head) > screenHeight(frame, frame.head))
    assert(screenHeight(middle, middle.head) < screenHeight(middle, rate))
    const settled = draw(11200)
    close(screenHeight(settled, settled.head), screenHeight(settled, rate), 'the finite display transition finishes after 200 ms')
    assert.deepEqual(history.samples, original)
})

test('normal one-second sampling and 500 ms jitter keep the latest-value indicator at the right edge', () => {
    const history = new ThroughputHistory()
    const motion = new ThroughputMotion()
    let sourceAt = 0
    for (let now = 0; now <= 14000; now += 25) {
        while (sourceAt + 500 <= now) {
            history.feed({ uploadTotal: sourceAt, downloadTotal: sourceAt * 3 }, sourceAt)
            sourceAt += 1000
        }
        const frame = motion.frame(history.samples, now, false)
        if (now >= 2000) {
            close(rateSampleX(frame.head.at, frame.endAt, 1080), 1080, 'the latest-value head never resets one sample to the right')
            assert.equal(frame.samples.at(-1), history.samples.at(-1), 'the current real observation stays available during the transition')
            assertJoined(frame)
            if (now - history.samples.at(-1).at >= 700) close(screenHeight(frame, frame.head), screenHeight(frame, history.samples.at(-1)), 'the endpoint settles 200 ms after the delayed delivery')
        }
    }
})

test('left guard eviction preserves the visible quadratic rather than moving its start inward', () => {
    const history = Array.from({ length: 101 }, (_, i) => sample(i * 1000, 200 + (i % 7) * 100))
    const motion = new ThroughputMotion()
    const before = motion.frame(history, 103000, true)
    pruneRateSamples(history, 103001)
    const after = motion.frame(history, 103001, true)
    close(after.paths[0].curves[0].from.at, after.endAt - RATE_WINDOW_MS, 'the curve clips exactly at the left edge')
    for (const at of [43050, 43200, 43700, 44200]) close(valueAt(before, at), valueAt(after, at), 'guard removal leaves old waveform geometry unchanged')
})

test('offscreen controls do not enter tooltips or inflate the visible scale', () => {
    const history = Array.from({ length: 68 }, (_, i) => sample((i - 64) * 1000, i < 2 || i > 65 ? 1e9 : 100))
    const frame = new ThroughputMotion().frame(history, 0, true)
    assert(frame.samples.every(sample => sample.at >= -RATE_WINDOW_MS && sample.at <= 0))
    assert(frame.samples.every(sample => sample.down === 100))
    assert.equal(frame.ymax, 1024 * 1.2)
    assert.equal(frame.tail, undefined, 'plain historical records are not a live stream')
})

test('a mutable bucket publishes its real value immediately while older curves stay unchanged', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    const before = motion.frame(history.samples, 70000, false)
    pruneRateSamples(history.samples, 70100)
    const count = history.samples.length
    history.feed({ uploadTotal: 145000, downloadTotal: 750000 }, 70100)
    const after = motion.frame(history.samples, 70100, false)
    assert.equal(history.samples.length, count, 'the existing weighted bucket is replaced, not duplicated')
    assert.notEqual(after.samples.at(-1).down, before.samples.at(-1).down)
    assert.equal(after.samples.at(-1), history.samples.at(-1))
    close(screenHeight(after, after.head), screenHeight(before, before.head), 'the display starts at its previous screen height even when the peak changes')
    assertJoined(after)
    assert.equal(rateHistoryState(history.samples).pending, history.samples.at(-1))
    for (const at of [60000, 65000, 68000]) close(valueAt(after, at), valueAt(before, at), 'only the nearby real-time tail can change')
})

test('display-tail holding and fading never add a synthetic point to measurement history', () => {
    const history = populated(10000)
    const original = structuredClone(history.samples)
    const motion = new ThroughputMotion()
    const held = motion.frame(history.samples, 11000, false)
    assert.equal(held.tail.from.at, 10000)
    assert.equal(held.tail.to.at, 11000)
    assert.equal(held.tail.opacity, 1)
    assert.equal(held.samples.at(-1).at, 10000, 'tooltip time is the true sample time')
    const fading = motion.frame(history.samples, 11750, false)
    close(fading.tail.opacity, 0.5, 'only the display extension fades')
    assert.equal(fading.head.down, history.samples.at(-1).down)
    const expired = motion.frame(history.samples, 12000, false)
    assert.equal(expired.tail, undefined)
    assert.equal(expired.head, undefined, 'expiry does not move a live indicator backwards')
    assert.equal(expired.paths.at(-1).curves.at(-1).to.at, 10000, 'missing time stays blank')
    assert.deepEqual(history.samples, original)
})

test('late source samples do not restart tail freshness from their drawing or delivery callback', () => {
    const history = populated(10000)
    const motion = new ThroughputMotion()
    const delayed = motion.frame(history.samples, 13000, false)
    assert.equal(delayed.tail, undefined)
    assert.equal(delayed.head, undefined)
    assert.equal(delayed.samples.at(-1), history.samples.at(-1))
    const redrawn = motion.frame(history.samples, 13040, false)
    assert.equal(redrawn.tail, undefined)
    assert.equal(redrawn.paths.at(-1).curves.at(-1).to.at, 10000)
})

test('resetBaseline removes a display tail immediately and a new segment never joins the old one', () => {
    const history = populated(10000)
    const motion = new ThroughputMotion()
    assert(motion.frame(history.samples, 10000, false).tail)
    history.resetBaseline()
    assert.equal(motion.frame(history.samples, 10040, false).tail, undefined)
    history.feed({ uploadTotal: 100000, downloadTotal: 100000 }, 12000)
    assert.equal(motion.frame(history.samples, 12000, false).tail, undefined, 'the first reconnect snapshot is only a baseline')
    history.feed({ uploadTotal: 100100, downloadTotal: 100700 }, 13000)
    const resumed = motion.frame(history.samples, 13000, false)
    assert.equal(resumed.head.down, 700)
    assert.notEqual(resumed.tail.segment, resumed.paths[0].segment)
    history.feed({ uploadTotal: 100200, downloadTotal: 101400 }, 14000)
    const continued = motion.frame(history.samples, 14000, false)
    assert.equal(continued.paths.length, 2)
    assert(continued.paths[0].curves.at(-1).to.at < continued.paths[1].curves[0].from.at)
    assert.equal(continued.tail.segment, continued.paths[1].segment)
})

test('negative phase preserves historical motion and projects the newest real value into the same frame', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    const before = motion.frame(history.samples, 70000, false)
    const oldPoint = history.samples.find(sample => sample.at === 55000)
    const oldX = rateSampleX(oldPoint.at, before.endAt, 600)
    history.shiftTime(-800)
    const measured = history.feed({ uploadTotal: 160000, downloadTotal: 800000 }, 70040)
    const real = structuredClone(history.samples)
    const after = motion.frame(history.samples, 70040, false)
    close(after.endAt, 69250, 'the existing historical phase is not reset to expose the new point')
    close(rateSampleX(after.projectAt(oldPoint.at), after.endAt, 600) - oldX, -0.5, 'history moves by the elapsed frame plus bounded slew')
    assert.equal(after.samples.at(-1).down, measured.down)
    assert.equal(after.samples.at(-1).up, measured.up)
    for (const key of ['up', 'down']) close(screenHeight(after, after.head, key), screenHeight(before, before.head, key), 'clock projection does not restart the endpoint at a new screen height')
    assertJoined(after)
    close(after.head.at, after.endAt, 'new data is present at the right edge immediately')
    assert(after.samples.includes(history.samples.at(-1)), 'tooltip keeps the newly received real object')
    assert(after.samples.every(sample => history.samples.includes(sample)))
    assert.deepEqual(history.samples, real, 'projection does not replace measured timestamps or records')
    const reference = new ThroughputMotion().frame(history.samples, 70040, true)
    const anchor = after.endAt - 1000
    for (const at of [60000, 67000, anchor - 10]) {
        close(after.projectAt(at), at, 'projection does not move the historical prefix')
        close(valueAt(after, at), valueAt(reference, at), 'splitting retains the old prefix exactly')
    }
    for (const path of after.paths) {
        for (const curve of path.curves) {
            assert(curve.from.at <= curve.control.at + 1e-5 && curve.control.at <= curve.to.at + 1e-5, 'projected control times are monotonic')
        }
    }
    const settled = motion.frame(history.samples, 70240, false)
    close(screenHeight(settled, settled.head), screenHeight(settled, measured), 'the projected endpoint completes its short transition')
    assertJoined(settled)
})

test('local projection is continuous at its anchor and when phase returns to the real-time clock', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    motion.frame(history.samples, 70000, false)
    history.shiftTime(-400)
    history.feed({ uploadTotal: 140800, downloadTotal: 704000 }, 70020)
    let last
    for (let now = 70020; now <= 70600; now += 5) {
        const frame = motion.frame(history.samples, now, false)
        const anchor = frame.endAt - 1000
        close(frame.projectAt(anchor), anchor, 'the projection is anchored without a position step')
        assert(Number.isFinite(frame.projectAt(70020)))
        assert(frame.projectAt(anchor + 1) >= frame.projectAt(anchor))
        close(frame.tail.to.at, frame.endAt, 'the displayed head stays on the current edge')
        if (last) {
            for (const at of [69000, 69500, 70020]) {
                assert(Math.abs(frame.projectAt(at) - last.projectAt(at)) < 10, 'projection changes continuously across the identity boundary')
            }
        }
        last = frame
    }
    assert.equal(last.projectAt(70020), 70020, 'once the source point fits, projection becomes identity')
})

test('irregular sample spacing cannot spread clock projection into older history', () => {
    const history = new ThroughputHistory()
    let total = 0
    for (const at of [48000, 50000, 59000, 66000, 67000, 69800]) {
        total += 10000
        history.feed({ uploadTotal: total, downloadTotal: total }, at)
    }
    const motion = new ThroughputMotion()
    motion.frame(history.samples, 69800, false)
    history.shiftTime(-900)
    history.feed({ uploadTotal: total + 9000, downloadTotal: total + 19000 }, 69900)
    const frame = motion.frame(history.samples, 69900, false)
    const reference = new ThroughputMotion().frame(history.samples, 69900, true)
    // The recent measurement interval may animate; the independent historical
    // prefix must still retain its original geometry with sparse samples.
    for (const at of [60000, 65000, 67000]) close(valueAt(frame, at), valueAt(reference, at), 'the independent historical prefix stays unchanged')
    assert.equal(frame.samples.at(-1), history.samples.at(-1))
    assertJoined(frame)
    const settled = motion.frame(history.samples, 70100, false)
    close(screenHeight(settled, settled.head), screenHeight(settled, history.samples.at(-1)), 'sparse sampling does not extend the finite transition')
})

test('small negative phase and simultaneous pruning retain the left edge within 64 samples', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    motion.frame(history.samples, 70000, false)
    history.shiftTime(-900)
    pruneRateSamples(history.samples, 70040)
    const frame = motion.frame(history.samples, 70040, false)
    close(frame.paths[0].curves[0].from.at, frame.endAt - RATE_WINDOW_MS, 'the compensated window retains its controls')
    assert(history.samples.length <= RATE_SAMPLE_LIMIT)
})

test('large clock changes and resume rebuild current time without replaying old frames', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    motion.frame(history.samples, 70000, false)
    history.shiftTime(-60000)
    const corrected = motion.frame(history.samples, 70040, false)
    assert.equal(corrected.endAt, 70040)
    assert.equal(corrected.tail, undefined)
    history.shiftTime(-500)
    motion.frame(history.samples, 70080, false)
    motion.reset()
    const resumed = motion.frame(history.samples, 70120, false)
    assert.equal(resumed.endAt, 70120)
    assert.equal(resumed.head, undefined)
})

test('reduced-motion retirement always schedules a future deadline and clears all guards', () => {
    const history = populated()
    const motion = new ThroughputMotion()
    let now = 70000
    let frames = 0
    while (true) {
        pruneRateSamples(history.samples, now)
        const frame = motion.frame(history.samples, now, true)
        if (frame.nextChangeAt === undefined) break
        assert(frame.nextChangeAt > now, 'guards and fading tails cannot schedule an expired deadline')
        now = frame.nextChangeAt + 1
        assert(++frames < 160, 'retirement is bounded instead of running a 1 ms timer loop')
    }
    assert.equal(history.samples.length, 0)
    const empty = motion.frame(history.samples, now, true)
    assert.equal(empty.active, false)
    assert.equal(empty.tail, undefined)
    assert(now < 70000 + RATE_WINDOW_MS + RATE_CLOCK_MARGIN_MS + 10)
})

test('rising and falling endpoints take visible intermediate positions with a fixed existing axis', () => {
    for (const [initial, target] of [[0.5, 2.5], [2.5, 0]]) {
        const { history, feed, frame: draw } = liveFixture(initial * MiB)
        const before = draw(11000)
        const reading = feed(11000, target * MiB)
        const first = draw(11000)
        const start = screenHeight(before, before.head)
        const goal = screenHeight(first, reading)
        close(screenHeight(first, first.head), start, 'a new target does not move the first displayed frame')
        let previous = start
        let intermediate = 0
        for (const now of [11040, 11080, 11120, 11160, 11200]) {
            const frame = draw(now)
            close(frame.ymax, first.ymax, 'the older peak keeps this movement independent of scale changes')
            const position = screenHeight(frame, frame.head)
            assert(position >= Math.min(start, goal) - 1e-6 && position <= Math.max(start, goal) + 1e-6, 'the endpoint does not overshoot')
            assert((position - previous) * (goal - start) >= -1e-6, 'the endpoint moves toward the current target')
            const progress = (position - start) / (goal - start)
            if (progress > 0.05 && progress < 0.95) intermediate++
            previous = position
            assertJoined(frame)
            assert.equal(frame.samples.at(-1), history.samples.at(-1))
        }
        assert(intermediate >= 2, 'the change is a visible transition rather than a deferred single jump')
        close(previous, goal, 'the endpoint reaches its target by 200 ms, including zero traffic')
    }
})

test('a peak above the current scale does not create a starting hump or a finishing rebound', () => {
    const { history, feed, frame: draw } = liveFixture(0.5 * MiB, 0.5 * MiB)
    const before = draw(11000)
    feed(11000, 32 * MiB)
    const real = structuredClone(history.samples)
    const first = draw(11000)
    assert(history.samples.at(-1).down > first.ymax, 'the fixture actually exercises clipping at the top of the plot')
    close(screenHeight(first, first.head), screenHeight(before, before.head), 'axis expansion cannot reproject the old endpoint into a jump')
    const top = screenHeight(first, first.head)
    for (const curve of first.paths.at(-1).curves.slice(-2)) {
        for (const point of [curve.from, curve.control, curve.to]) {
            assert(screenHeight(first, point) <= top + 1e-6, 'the newly connected curve cannot jump above the retained endpoint')
        }
    }
    for (const now of [11040, 11080, 11120, 11160, 11200]) assertJoined(draw(now))
    const settled = draw(11200)
    const raw = new ThroughputMotion().frame(history.samples, 11200, true)
    const actualCurves = settled.paths.at(-1).curves.slice(-2)
    const rawCurves = raw.paths.at(-1).curves.slice(-2)
    for (let index = 0; index < actualCurves.length; index++) {
        for (const part of ['from', 'control', 'to']) {
            close(screenHeight(settled, actualCurves[index][part]), screenHeight(settled, rawCurves[index][part]), 'completed geometry exactly matches the raw curve at the same scale')
        }
    }
    assert.deepEqual(history.samples, real, 'large peaks remain real measurements throughout the display transition')
})

test('a same-bucket reversal continues the currently displayed midpoint as well as the head', () => {
    const { history, feed, frame: draw } = liveFixture(0.5 * MiB, 0.5 * MiB)
    feed(10980, 0.5 * MiB)
    draw(10980)
    feed(11000, 32 * MiB)
    draw(11000)
    const before = draw(11100)
    const beforeLast = before.paths.at(-1).curves.at(-1)
    const count = history.samples.length
    feed(11100, 0.1 * MiB)
    const real = structuredClone(history.samples)
    const after = draw(11100)
    const afterLast = after.paths.at(-1).curves.at(-1)
    assert.equal(history.samples.length, count, 'the new observation replaces the current weighted second bucket')
    assert.equal(after.samples.at(-1), history.samples.at(-1))
    for (const part of ['from', 'control', 'to']) {
        close(screenHeight(after, afterLast[part]), screenHeight(before, beforeLast[part]), 'retargeting preserves every displayed terminal position at zero elapsed time')
    }
    close(screenHeight(after, after.head), screenHeight(before, before.head), 'the head also remains continuous')
    assertJoined(after)
    for (const now of [11140, 11180, 11220, 11260, 11300]) assertJoined(draw(now))
    const settled = draw(11300)
    close(screenHeight(settled, settled.head), screenHeight(settled, history.samples.at(-1)), 'the newest target finishes on its own finite deadline')
    assert.deepEqual(history.samples, real)
})

test('an unfinished bucket keeps its shared join and original deadline when the next bucket arrives', () => {
    const { history, feed, frame: draw } = liveFixture()
    feed(11900, 3 * MiB)
    draw(11900)
    const before = draw(12000)
    const oldMiddle = before.paths.at(-1).curves.at(-1).from
    const oldSample = history.samples.at(-1)
    feed(12000, 0.5 * MiB)
    const after = draw(12000)
    close(screenHeightAt(after, oldMiddle.at), screenHeight(before, oldMiddle), 'the promoted midpoint keeps the height shared with the preceding curve')
    const promoted = after.paths.at(-1).curves.find(curve => Math.abs(curve.control.at - oldSample.at) < 1e-5)
    assert(promoted, 'the previous measured endpoint becomes a control of the new local curve')
    close(screenHeight(after, promoted.control), screenHeight(before, before.head), 'promoting the old endpoint does not snap its displayed control height')
    assertJoined(after)

    draw(12040)
    feed(12040, 0.25 * MiB)
    assertJoined(draw(12040))
    const originalDeadline = draw(12100)
    const raw = new ThroughputMotion().frame(history.samples, 12100, true)
    close(screenHeightAt(originalDeadline, oldMiddle.at), screenHeightAt(raw, oldMiddle.at), 'new targets do not restart the previous bucket deadline')
    const finishedControl = originalDeadline.paths.at(-1).curves.find(curve => Math.abs(curve.control.at - oldSample.at) < 1e-5)
    close(screenHeight(originalDeadline, finishedControl.control), screenHeight(originalDeadline, oldSample), 'the old endpoint control also completes on its original deadline')
    const settled = draw(12240)
    close(screenHeight(settled, settled.head), screenHeight(settled, history.samples.at(-1)), 'the current bucket completes independently')
    assertJoined(settled)
})

test('reduced motion and a stopped drawing lifecycle discard in-flight endpoint transitions', () => {
    const { history, motion, feed, frame: draw } = liveFixture()
    feed(11000, 2.5 * MiB)
    draw(11000)
    const middle = draw(11080)
    assert(screenHeight(middle, middle.head) < screenHeight(middle, history.samples.at(-1)))
    const reduced = motion.frame(history.samples, 11080, true)
    close(screenHeight(reduced, reduced.head), screenHeight(reduced, history.samples.at(-1)), 'reduced motion displays the current target immediately')
    const normal = draw(11080)
    close(screenHeight(normal, normal.head), screenHeight(normal, history.samples.at(-1)), 'leaving reduced motion does not replay the interrupted transition')

    feed(12000, 0)
    draw(12000)
    draw(12080)
    motion.reset()
    const resumed = draw(12080)
    close(screenHeight(resumed, resumed.head), 0, 'the existing visibility-stop reset initializes the latest value without catch-up')
    assertJoined(resumed)

    history.resetBaseline()
    const disconnected = draw(12100)
    assert.equal(disconnected.head, undefined)
    assert.equal(disconnected.tail, undefined)
})

test('the existing 0.4.10 axis response stays unchanged in both directions', () => {
    const motion = new ThroughputMotion()
    const history = [sample(0, 100), sample(1000, 100), sample(2000, 100)]
    let frame
    for (let i = 0; i < 120; i++) frame = motion.frame(history, 4000, false)
    const baseline = frame.ymax
    history.push(sample(3000, 1e8), sample(4000, 1e8), sample(5000, 1e8))
    const rising = motion.frame(history, 6000, false)
    close(rising.ymax, baseline + (1.2e8 - baseline) * 0.06, 'the requested live-tail change does not change axis expansion')
    history.splice(0, history.length, sample(0, 100), sample(1000, 100), sample(2000, 100))
    const falling = motion.frame(history, 6000, false)
    close(falling.ymax, rising.ymax + (1024 * 1.2 - rising.ymax) * 0.06, 'axis contraction is unchanged')
    close(motion.frame(history, 6000, true).ymax, 1024 * 1.2, 'reduced motion applies the target directly')
})
