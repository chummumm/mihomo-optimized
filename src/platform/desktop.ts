import { useEffect, useSyncExternalStore } from 'react'

import { DesktopError, desktopError, readDesktopStatus, type DesktopStatus, type DesktopUpdateProgress } from './model'

export function isDesktop () {
    return typeof __DESKTOP__ !== 'undefined' && __DESKTOP__
}

interface DesktopView {
    ready: boolean
    status: DesktopStatus | null
    error: DesktopError | null
    busy: string | null
    visible: boolean
    dirty: boolean
    writing: boolean
    exitRequested: boolean
    dialog: 'config' | 'import' | null
    updateProgress: DesktopUpdateProgress | null
}

let current: DesktopView = {
    ready: !isDesktop(), status: null, error: null, busy: null,
    visible: true, dirty: false, writing: false, exitRequested: false, dialog: null, updateProgress: null,
}
const subscribers = new Set<() => void>()
const drafts = new Set<string>()
const writes = new Set<string>()
let initializing: Promise<void> | undefined
let dirtyWrite = Promise.resolve()
let statusEvent = 0
let statusRead = 0

function publish (patch: Partial<DesktopView>) {
    current = { ...current, ...patch }
    for (const listener of subscribers) listener()
}

function subscribe (listener: () => void) {
    subscribers.add(listener)
    return () => { subscribers.delete(listener) }
}

export function desktopSnapshot () { return current }

export function useDesktop () {
    return useSyncExternalStore(subscribe, desktopSnapshot, desktopSnapshot)
}

export async function desktopInvoke<T> (command: string, args?: Record<string, unknown>): Promise<T> {
    if (!isDesktop()) throw new DesktopError('desktop_unavailable', 'Desktop connection unavailable')
    try {
        const { invoke } = await import('@tauri-apps/api/core')
        return await invoke<T>(command, args)
    } catch (error) {
        throw desktopError(error)
    }
}

export async function refreshDesktop () {
    const at = statusEvent
    const request = ++statusRead
    try {
        const status = readDesktopStatus(await desktopInvoke('desktop_status'))
        // An event received during the read is newer than this response.
        if (statusEvent === at && statusRead === request) publish({ status, ready: true, error: null })
    } catch (reason) {
        if (statusRead === request && statusEvent === at) publish({ ready: true, error: desktopError(reason) })
    }
}

export async function desktopAction (command: string, args?: Record<string, unknown>) {
    if (current.busy) throw new DesktopError('desktop_busy', 'Another desktop operation is in progress')
    publish({ busy: command, error: null })
    try {
        const value = await desktopInvoke<unknown>(command, args)
        if (value && typeof value === 'object' && 'phase' in value) publish({ status: readDesktopStatus(value) })
        else await refreshDesktop()
        return value
    } catch (reason) {
        const error = desktopError(reason)
        publish({ error })
        // Failure can still change actual OS/core state; refresh the switches.
        await refreshDesktop()
        publish({ error })
        throw error
    } finally {
        publish({ busy: null })
    }
}

function setVisibility (visible: boolean) {
    if (visible === current.visible) return
    document.documentElement.setAttribute('data-desktop-hidden', String(!visible))
    publish({ visible })
    document.dispatchEvent(new Event('visibilitychange'))
}

export async function initializeDesktop () {
    if (!isDesktop()) return
    if (initializing) return await initializing
    initializing = (async () => {
        try {
            const { listen } = await import('@tauri-apps/api/event')
            // Subscribe before the initial read to avoid losing startup events.
            await listen<DesktopStatus>('desktop-status', event => {
                try {
                    statusEvent++
                    publish({ status: readDesktopStatus(event.payload), ready: true, error: null })
                } catch (reason) { publish({ error: desktopError(reason), ready: true }) }
            })
            await listen<{ visible: boolean }>('desktop-visibility', event => {
                if (typeof event.payload?.visible === 'boolean') setVisibility(event.payload.visible)
            })
            await listen('desktop-exit-requested', () => { publish({ exitRequested: true }) })
            await listen<DesktopUpdateProgress>('desktop-update-progress', event => {
                publish({ updateProgress: event.payload })
            })
            document.addEventListener('click', event => {
                // Router links already handle their navigation at the React root.
                if (event.defaultPrevented) return
                const anchor = (event.target as Element | null)?.closest<HTMLAnchorElement>('a[href]')
                if (!anchor || !/^https?:\/\//.test(anchor.href)) return
                if (new URL(anchor.href).origin === window.location.origin) return
                event.preventDefault()
                void desktopInvoke('desktop_open_external', { url: anchor.href }).catch(reason => {
                    publish({ error: desktopError(reason) })
                })
            })
            await refreshDesktop()
        } catch (reason) {
            publish({ ready: true, error: desktopError(reason) })
        }
    })()
    return await initializing
}

export function setDesktopDirty (id: string, dirty: boolean, writing = false) {
    if (!isDesktop()) return
    if (dirty) drafts.add(id)
    else drafts.delete(id)
    if (writing) writes.add(id)
    else writes.delete(id)
    const next = drafts.size > 0 || writes.size > 0
    const changed = next !== current.dirty
    if (changed || (writes.size > 0) !== current.writing) publish({ dirty: next, writing: writes.size > 0 })
    if (!changed) return
    // Serialize writes so a delayed earlier clean state cannot clear a new draft.
    dirtyWrite = dirtyWrite.catch(() => undefined).then(async () => {
        await desktopInvoke('desktop_set_dirty', { dirty: next })
    }).catch(reason => { publish({ error: desktopError(reason) }) })
}

export function useDesktopDirty (id: string, dirty: boolean, writing = false) {
    useEffect(() => {
        setDesktopDirty(id, dirty, writing)
    }, [id, dirty, writing])
    useEffect(() => () => { setDesktopDirty(id, false) }, [id])
}

export function cancelDesktopExit () { publish({ exitRequested: false }) }

export function setDesktopDialog (dialog: DesktopView['dialog']) { publish({ dialog }) }

export async function confirmDesktopExit () {
    await dirtyWrite
    await desktopInvoke('desktop_exit', { force: true })
}

export async function invokeConfig<T> (command: string, args?: Record<string, unknown>, signal?: AbortSignal) {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    const result = await desktopInvoke<T>(command, args)
    // The host may finish a write after cancellation. Callers never publish a
    // result into an unmounted editor or a newer document session.
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    return result
}
