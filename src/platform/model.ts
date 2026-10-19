export type DesktopPhase = 'stopped' | 'starting' | 'running' | 'stopping' | 'error'

export interface DesktopStatus {
    appVersion?: string
    phase: DesktopPhase
    systemProxy: boolean
    tun: boolean
    autostart: boolean
    controller: { url: string, secret: string } | null
    lastError: string | null
    coreVersion: string | null
    configPath: string
    logPath: string
    service: { installed: boolean, running: boolean } | null
    capabilities: {
        systemProxy: boolean
        autostart: boolean
        tun: boolean
        configImport: boolean
        serviceManagement: boolean
    }
}

export interface DesktopConfigDocument {
    path: string
    content: string
    revision: string
    max_size: number
    writable: boolean
}

export interface DesktopUpdate {
    currentVersion: string
    available: boolean
    canInstall: boolean
    releaseUrl?: string
    version?: string
    publishedAt?: string
    body?: string
}

export interface DesktopUpdateProgress {
    stage: 'checking' | 'downloading' | 'installing' | 'ready' | 'error'
    downloaded?: number
    total?: number
    message?: string
}

export class DesktopError extends Error {
    constructor (readonly code: string, message: string) {
        super(message)
        this.name = 'DesktopError'
    }
}

export function desktopError (value: unknown): DesktopError {
    if (value instanceof DesktopError) return value
    if (value && typeof value === 'object' && 'code' in value && typeof value.code === 'string') {
        const message = 'message' in value && typeof value.message === 'string' ? value.message : value.code
        return new DesktopError(value.code, message.slice(0, 500))
    }
    // Unexpected IPC errors may contain host paths or configuration values.
    // Only the host's structured, sanitized error contract is user-facing.
    return new DesktopError('desktop_unavailable', 'Desktop connection unavailable')
}

export function desktopAPIInfo (status: DesktopStatus | null) {
    const inactive = { hostname: '127.0.0.1', port: '1', protocol: 'http:', secret: '', enabled: false }
    if (status?.phase !== 'running' || !status.controller) return inactive
    try {
        const url = new URL(status.controller.url)
        if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port ||
            url.username || url.password || url.search || url.hash || (url.pathname !== '/' && url.pathname !== '') ||
            !status.controller.secret) return inactive
        return { hostname: url.hostname, port: url.port, protocol: url.protocol, secret: status.controller.secret, enabled: true }
    } catch {
        return inactive
    }
}

export function readDesktopStatus (value: unknown): DesktopStatus {
    if (!value || typeof value !== 'object') throw new DesktopError('invalid_response', 'Invalid desktop status')
    const status = value as Partial<DesktopStatus>
    if (!['stopped', 'starting', 'running', 'stopping', 'error'].includes(status.phase ?? '') ||
        typeof status.systemProxy !== 'boolean' || typeof status.tun !== 'boolean' || typeof status.autostart !== 'boolean' ||
        typeof status.configPath !== 'string' || typeof status.logPath !== 'string' ||
        !status.capabilities || ['systemProxy', 'autostart', 'tun', 'configImport', 'serviceManagement'].some(key =>
            typeof status.capabilities?.[key as keyof DesktopStatus['capabilities']] !== 'boolean') ||
        (status.lastError !== null && typeof status.lastError !== 'string') ||
        (status.coreVersion !== null && typeof status.coreVersion !== 'string') ||
        (status.appVersion !== undefined && typeof status.appVersion !== 'string') ||
        (status.service !== null && (!status.service || typeof status.service.installed !== 'boolean' || typeof status.service.running !== 'boolean'))) {
        throw new DesktopError('invalid_response', 'Invalid desktop status')
    }
    if (status.controller !== null && (!status.controller || typeof status.controller.url !== 'string' || typeof status.controller.secret !== 'string')) {
        throw new DesktopError('invalid_response', 'Invalid desktop controller')
    }
    if (status.phase === 'running' && !desktopAPIInfo(status as DesktopStatus).enabled) {
        throw new DesktopError('invalid_response', 'Invalid local desktop controller')
    }
    return status as DesktopStatus
}
