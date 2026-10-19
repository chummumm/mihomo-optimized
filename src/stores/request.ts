import { atom, useAtom, useAtomValue } from 'jotai'
import { atomWithStorage } from 'jotai/utils'
import { useLocation } from 'react-router-dom'

import { controllerProtocol } from '@lib/controller'
import { isIP } from '@lib/ip'
import { isClashX, jsBridge } from '@lib/jsBridge'
import { Client } from '@lib/request'

import { isDesktop, useDesktop } from '../platform/desktop'
import { desktopAPIInfo } from '../platform/model'

const clashxConfigAtom = atom(async () => {
    if (!isClashX()) {
        return null
    }

    const info = await jsBridge!.getAPIInfo()
    return {
        hostname: info.host,
        port: info.port,
        secret: info.secret,
        protocol: 'http:',
    }
})

function getControllerFromLocation (): string | null {
    if (!isIP(location.hostname)) {
        return null
    }

    if (location.port !== '') {
        return JSON.stringify([
            {
                hostname: location.hostname,
                port: +location.port,
                secret: '',
            },
        ])
    }

    const port = location.protocol === 'https:' ? 443 : 80
    return JSON.stringify([
        {
            hostname: location.hostname,
            port,
            secret: '',
        },
    ])
}

// jotai v2 use initialValue first avoid hydration warning, but we don't want that
const hostsStorageOrigin = isDesktop() ? '[]' : localStorage.getItem('externalControllers') ??
    getControllerFromLocation() ??
    '[{ "hostname": "127.0.0.1", "port": 7890, "secret": "" }]'
const hostSelectIdxStorageOrigin = isDesktop() ? '0' : localStorage.getItem('externalControllerIndex') ?? '0'

type ControllerHosts = Array<{
    hostname: string
    port: string
    secret: string
}>
export const hostsStorageAtom = isDesktop()
    ? atom<ControllerHosts>([])
    : atomWithStorage<ControllerHosts>('externalControllers', JSON.parse(hostsStorageOrigin))
export const hostSelectIdxStorageAtom = isDesktop()
    ? atom(0)
    : atomWithStorage<number>('externalControllerIndex', parseInt(hostSelectIdxStorageOrigin))

export function useAPIInfo () {
    const desktop = useDesktop()
    const clashx = useAtomValue(clashxConfigAtom)
    const location = useLocation()
    const hostSelectIdxStorage = useAtomValue(hostSelectIdxStorageAtom)
    const hostsStorage = useAtomValue(hostsStorageAtom)

    if (isDesktop()) return desktopAPIInfo(desktop.status)

    if (clashx != null) {
        return { ...clashx, enabled: true }
    }

    let url: URL | undefined
    {
        const meta = document.querySelector<HTMLMetaElement>('meta[name="external-controller"]')
        if ((meta?.content?.match(/^https?:/)) != null) {
            // [protocol]://[secret]@[hostname]:[port]
            url = new URL(meta.content)
        }
    }

    const qs = new URLSearchParams(location.search)

    const hostname = qs.get('host') ?? hostsStorage?.[hostSelectIdxStorage]?.hostname ?? url?.hostname ?? '127.0.0.1'
    const port = qs.get('port') ?? hostsStorage?.[hostSelectIdxStorage]?.port ?? url?.port ?? '9090'
    const secret = qs.get('secret') ?? hostsStorage?.[hostSelectIdxStorage]?.secret ?? url?.username ?? ''
    const protocol = controllerProtocol(qs.get('protocol'), hostname, url?.protocol, window.location.protocol)

    return { hostname, port, secret, protocol, enabled: true }
}

const clientAtom = atom({
    key: '',
    instance: null as Client | null,
})

// Controller-owned stores depend on the active client without exposing the
// connection key (which also contains the controller secret).
export const currentClientAtom = atom(get => get(clientAtom).instance)

export function useClient () {
    const {
        hostname,
        port,
        secret,
        protocol,
        enabled,
    } = useAPIInfo()

    const [item, setItem] = useAtom(clientAtom)
    const key = `${protocol}//${hostname}:${port}?secret=${secret}&enabled=${enabled}`
    if (item.key === key) {
        return item.instance!
    }

    const client = new Client(`${protocol}//${hostname}:${port}`, secret, enabled)
    setItem({ key, instance: client })

    return client
}

// Editing the persistent source file must survive a managed core restart.
// HTTP clients still rotate to invalidate stale data for the running controller.
const nativeConfigClient = new Client('http://127.0.0.1:1', undefined, false)

export function useConfigClient () {
    const client = useClient()
    return isDesktop() ? nativeConfigClient : client
}
