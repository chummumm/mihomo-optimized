import { useEffect, useRef } from 'react'

import { formatRate } from '@components'

import { ThroughputMotion } from './chart-motion'
import { pruneRateSamples, rateSampleX, type RateSample } from './throughput-history'

interface ThroughputChartProps {
    samples: React.MutableRefObject<RateSample[]>
    updatedAt: number
    label: string
    now: string
    names: [string, string]
}

interface Palette {
    down: string
    up: string
    grid: string
    label: string
    cross: string
    tip: string
    tipText: string
    dark: boolean
}

function withAlpha (color: string, alpha: number) {
    const hex = color.trim().match(/^#([0-9a-f]{6})$/i)
    if (hex === null) return color
    const v = parseInt(hex[1], 16)
    return `rgba(${v >> 16}, ${(v >> 8) & 255}, ${v & 255}, ${alpha})`
}

export function ThroughputChart ({ samples, updatedAt, label, now: nowLabel, names }: ThroughputChartProps) {
    const canvas = useRef<HTMLCanvasElement>(null)
    const labels = useRef({ nowLabel, names })
    const redraw = useRef<() => void>()
    labels.current = { nowLabel, names }

    useEffect(() => {
        const element = canvas.current
        if (element === null) return
        const ctx = element.getContext('2d')
        if (ctx === null) return
        const reduce = window.matchMedia('(prefers-reduced-motion: reduce)')
        const scheme = window.matchMedia('(prefers-color-scheme: dark)')
        const motion = new ThroughputMotion()
        let palette: Palette | null = null
        let hoverX: number | null = null
        let frame = 0
        let visible = true
        let dirty = true
        let hasDrawingWork = false
        let nextChangeAt: number | undefined
        let expiration: ReturnType<typeof setTimeout> | undefined

        const readPalette = () => {
            const style = getComputedStyle(element)
            const read = (name: string) => style.getPropertyValue(name).trim()
            const tip = read('--panel-solid')
            const hex = tip.match(/^#([0-9a-f]{6})$/i)
            const v = hex === null ? 0 : parseInt(hex[1], 16)
            const lum = (0.2126 * (v >> 16) + 0.7152 * ((v >> 8) & 255) + 0.0722 * (v & 255)) / 255
            return { down: read('--accent'), up: read('--accent-2'), grid: read('--hair'), label: read('--faint'), cross: read('--hair-2'), tip, tipText: read('--text'), dark: lum < 0.45 }
        }

        const draw = (now: number) => {
            hasDrawingWork = false
            nextChangeAt = undefined
            const w = element.clientWidth
            const h = element.clientHeight
            if (w === 0 || h === 0) return
            const dpr = Math.min(2, window.devicePixelRatio || 1)
            if (element.width !== Math.round(w * dpr) || element.height !== Math.round(h * dpr)) {
                element.width = Math.round(w * dpr)
                element.height = Math.round(h * dpr)
            }
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
            palette ??= readPalette()
            const c = palette
            const history = samples.current
            pruneRateSamples(history, now)
            ctx.clearRect(0, 0, w, h)
            const compact = h < 120
            const padTop = compact ? 6 : 16
            const padBottom = compact ? 4 : 20
            const height = h - padTop - padBottom
            const plotWidth = Math.max(0, w - 3)
            const latest = history[history.length - 1]
            const progress = reduce.matches || latest === undefined ? 1 : Math.max(0, Math.min(1, (now - latest.at) / 1000))
            const drawing = motion.frame(history, now, reduce.matches)
            const { endAt, ymax, head, paths } = drawing
            hasDrawingWork = drawing.active
            nextChangeAt = drawing.nextChangeAt

            ctx.font = '10px "Martian Mono", ui-monospace, monospace'
            ctx.lineWidth = 1
            const rows = compact ? 2 : 3
            for (let i = 1; i <= rows; i++) {
                const y = Math.round(padTop + height - (height * i) / rows) + 0.5
                ctx.strokeStyle = c.grid
                ctx.setLineDash([2, 4])
                ctx.beginPath()
                ctx.moveTo(0, y)
                ctx.lineTo(w, y)
                ctx.stroke()
                ctx.setLineDash([])
                if (!compact) {
                    const text = formatRate((ymax * i) / rows)
                    ctx.fillStyle = c.label
                    ctx.textBaseline = 'bottom'
                    ctx.fillText(text, 4, y - 4)
                }
            }
            if (!compact) {
                ctx.fillStyle = c.label
                ctx.textBaseline = 'alphabetic'
                for (const s of [60, 45, 30, 15]) {
                    const age = Math.round((s + (now - endAt) / 1000) * 10) / 10
                    ctx.fillText(`-${age}s`, plotWidth - (s / 60) * plotWidth + 4, h - 5)
                }
                ctx.textAlign = 'right'
                ctx.fillText(labels.current.nowLabel, w - 4, h - 5)
                ctx.textAlign = 'left'
            }
            if (paths.length === 0) return
            const xs = (at: number) => rateSampleX(at, endAt, plotWidth)
            const ys = (v: number) => padTop + height - (Math.min(v, ymax) / ymax) * height
            const series: Array<['down' | 'up', string, number, number]> = [['down', c.down, 2.2, 0.34], ['up', c.up, 1.8, 0.16]]
            ctx.save()
            ctx.beginPath()
            ctx.rect(0, padTop, w, height)
            ctx.clip()
            for (const [key, color, width, fill] of series) {
                const path = new Path2D()
                const area = new Path2D()
                for (const { curves } of paths) {
                    const first = curves[0].from
                    const last = curves[curves.length - 1].to
                    path.moveTo(xs(first.at), ys(first[key]))
                    area.moveTo(xs(first.at), ys(first[key]))
                    for (const curve of curves) {
                        const cx = xs(curve.control.at)
                        const cy = ys(curve.control[key])
                        const x = xs(curve.to.at)
                        const y = ys(curve.to[key])
                        path.quadraticCurveTo(cx, cy, x, y)
                        area.quadraticCurveTo(cx, cy, x, y)
                    }
                    area.lineTo(xs(last.at), padTop + height)
                    area.lineTo(xs(first.at), padTop + height)
                    area.closePath()
                }
                const gradient = ctx.createLinearGradient(0, padTop, 0, padTop + height)
                gradient.addColorStop(0, withAlpha(color, fill))
                gradient.addColorStop(1, withAlpha(color, 0))
                ctx.fillStyle = gradient
                ctx.fill(area)
                ctx.save()
                if (c.dark) {
                    ctx.shadowColor = color
                    ctx.shadowBlur = 6
                }
                ctx.strokeStyle = color
                ctx.lineWidth = width
                ctx.lineJoin = 'round'
                ctx.stroke(path)
                ctx.restore()

                // The head is the intersection with a known, stable curve. If
                // input stops it ages leftwards; missing time is never filled.
                if (head !== undefined) {
                    const headX = xs(head.at)
                    const headY = ys(head[key])
                    ctx.fillStyle = color
                    ctx.beginPath()
                    ctx.arc(headX, headY, 3, 0, Math.PI * 2)
                    ctx.fill()
                    if (!reduce.matches && progress < 1) {
                        ctx.fillStyle = withAlpha(color, 0.25 * (1 - progress))
                        ctx.beginPath()
                        ctx.arc(headX, headY, 3 + 9 * progress, 0, Math.PI * 2)
                        ctx.fill()
                    }
                }
            }
            ctx.restore()

            if (hoverX !== null && drawing.samples.length > 0) {
                let best = 0
                let distance = Infinity
                drawing.samples.forEach((sample, i) => {
                    const d = Math.abs(xs(sample.at) - hoverX!)
                    if (d < distance) {
                        distance = d
                        best = i
                    }
                })
                const selected = drawing.samples[best]
                const x = xs(selected.at)
                ctx.strokeStyle = c.cross
                ctx.setLineDash([3, 3])
                ctx.beginPath()
                ctx.moveTo(x + 0.5, padTop)
                ctx.lineTo(x + 0.5, padTop + height)
                ctx.stroke()
                ctx.setLineDash([])
                const lines: Array<[string, string]> = [
                    [c.down, `${labels.current.names[0]} ${formatRate(selected.down)}`],
                    [c.up, `${labels.current.names[1]} ${formatRate(selected.up)}`],
                ]
                ctx.font = '11px "Martian Mono", ui-monospace, monospace'
                const boxW = Math.max(...lines.map(([, text]) => ctx.measureText(text).width)) + 28
                const boxH = 14 + lines.length * 16
                const bx = Math.min(w - boxW - 6, x + 10)
                const by = padTop + 4
                ctx.fillStyle = c.tip
                ctx.strokeStyle = c.cross
                ctx.beginPath()
                if (typeof ctx.roundRect === 'function') ctx.roundRect(bx, by, boxW, boxH, 8)
                else ctx.rect(bx, by, boxW, boxH)
                ctx.fill()
                ctx.stroke()
                lines.forEach(([color, text], i) => {
                    ctx.fillStyle = color
                    ctx.fillRect(bx + 9, by + 13 + i * 16, 9, 2)
                    ctx.fillStyle = c.tipText
                    ctx.textBaseline = 'middle'
                    ctx.fillText(text, bx + 23, by + 14 + i * 16)
                })
                for (const [key, color] of [['down', c.down], ['up', c.up]] as const) {
                    ctx.fillStyle = color
                    ctx.beginPath()
                    ctx.arc(x, ys(selected[key]), 3.5, 0, Math.PI * 2)
                    ctx.fill()
                }
            }
        }

        // Data and pointer changes share the same thirty-frame drawing limit.
        // Normal motion keeps the time axis flowing between measurements.
        // Reduced motion schedules a single frame for changes, with no idle RAF loop.
        let lastFrame: number | undefined
        const loop = (now: number) => {
            frame = 0
            if (!visible || document.hidden || !element.isConnected) return
            if (lastFrame === undefined || now - lastFrame >= 1000 / 30) {
                draw(now)
                dirty = false
                lastFrame = now
            }
            if (dirty || (!reduce.matches && hasDrawingWork)) frame = requestAnimationFrame(loop)
            else if (nextChangeAt !== undefined) {
                // Guard points do not schedule already-expired deadlines.
                const delay = Math.max(1, nextChangeAt - now + 1)
                expiration = setTimeout(invalidate, delay)
            }
        }
        const invalidate = () => {
            dirty = true
            clearTimeout(expiration)
            if (frame === 0 && visible && !document.hidden) frame = requestAnimationFrame(loop)
        }
        const stop = () => {
            cancelAnimationFrame(frame)
            frame = 0
            clearTimeout(expiration)
            lastFrame = undefined
            motion.reset()
        }
        redraw.current = invalidate
        const observer = typeof IntersectionObserver === 'function'
            ? new IntersectionObserver(entries => {
                visible = entries.some(entry => entry.isIntersecting)
                if (visible) invalidate()
                else stop()
            })
            : undefined
        observer?.observe(element)
        const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(invalidate) : undefined
        resize?.observe(element)
        const onMove = (event: PointerEvent) => {
            hoverX = event.clientX - element.getBoundingClientRect().left
            invalidate()
        }
        const onLeave = () => {
            hoverX = null
            invalidate()
        }
        const onScheme = () => {
            palette = null
            invalidate()
        }
        const onVisibility = () => { if (document.hidden) stop(); else invalidate() }
        element.addEventListener('pointermove', onMove)
        element.addEventListener('pointerleave', onLeave)
        scheme.addEventListener('change', onScheme)
        reduce.addEventListener('change', invalidate)
        document.addEventListener('visibilitychange', onVisibility)
        invalidate()
        return () => {
            stop()
            redraw.current = undefined
            observer?.disconnect()
            resize?.disconnect()
            element.removeEventListener('pointermove', onMove)
            element.removeEventListener('pointerleave', onLeave)
            scheme.removeEventListener('change', onScheme)
            reduce.removeEventListener('change', invalidate)
            document.removeEventListener('visibilitychange', onVisibility)
        }
    }, [samples])

    useEffect(() => { redraw.current?.() }, [updatedAt, label, nowLabel])

    return <canvas ref={canvas} className="connections-chart" role="img" aria-label={label} />
}

