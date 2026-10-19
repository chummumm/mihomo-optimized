import axios from 'axios'
import classnames from 'classnames'
import { useAtom, useAtomValue, useSetAtom } from 'jotai'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'

import { Header, Card, Switch, Button, ButtonSelect, type ButtonSelectOptions, Input, Select, success, error } from '@components'
import { type Lang } from '@i18n'
import { useObject } from '@lib/hook'
import { jsBridge } from '@lib/jsBridge'
import { DEFAULT_PROBE_URL, normalizeProbeURL } from '@lib/probe'
import type { Config } from '@lib/request'
import { useI18n, useClashXData, useGeneral, useVersion, useClient, useConfig, identityAtom, hostSelectIdxStorageAtom, hostsStorageAtom, useAPIInfo } from '@stores'

import { DesktopAppUpdate, DesktopControls } from '../../platform/DesktopUI'
import { desktopAction, isDesktop as inDesktop, setDesktopDialog, useDesktop } from '../../platform/desktop'
import { desktopError } from '../../platform/model'

import { ConfigEditor } from './ConfigEditor'
import { UpdateCheck } from './UpdateCheck'
import './style.css'

const languageOptions: ButtonSelectOptions[] = [{ label: '中文', value: 'zh_CN' }, { label: 'English', value: 'en_US' }]

export default function Settings () {
    const desktop = useDesktop()
    const isDesktop = inDesktop()
    const coreOffline = isDesktop && desktop.status?.phase !== 'running'
    const { premium, version } = useVersion()
    const { data: clashXData, update: fetchClashXData } = useClashXData()
    const { general, update: fetchGeneral } = useGeneral()
    const setIdentity = useSetAtom(identityAtom)
    const [hostSelectIdx, setHostSelectIdx] = useAtom(hostSelectIdxStorageAtom)
    const hostsStorage = useAtomValue(hostsStorageAtom)
    const apiInfo = useAPIInfo()
    const location = useLocation()
    const controllerFromURL = useMemo(() => {
        const query = new URLSearchParams(location.search)
        return ['host', 'port', 'secret', 'protocol'].some(key => query.has(key))
    }, [location.search])
    const { translation, setLang, lang } = useI18n()
    const { t } = translation('Settings')
    const { t: td } = translation('Desktop')
    const client = useClient()
    const { data: settings, set: setSettings } = useConfig()
    const savedProbeURL = normalizeProbeURL(settings.probeURL) ?? DEFAULT_PROBE_URL
    const [probeURL, setProbeURL] = useState(savedProbeURL)
    const [reloading, setReloading] = useState(false)
    const [editorOpen, setEditorOpen] = useState(false)
    function openConfigEditor () {
        if (isDesktop) setDesktopDialog('config')
        else setEditorOpen(true)
    }
    const reloadRequest = useRef<AbortController | null>(null)
    const [info, set] = useObject({
        socks5ProxyPort: 7891,
        httpProxyPort: 7890,
        mixedProxyPort: 0,
    })

    useEffect(() => {
        set('socks5ProxyPort', general?.socksPort ?? 0)
        set('httpProxyPort', general?.port ?? 0)
        set('mixedProxyPort', general?.mixedPort ?? 0)
    }, [general, set])

    useEffect(() => { setProbeURL(savedProbeURL) }, [savedProbeURL])

    useEffect(() => { if (!isDesktop) setEditorOpen(false) }, [client, isDesktop])

    useEffect(() => {
        setReloading(false)
        return () => {
            reloadRequest.current?.abort()
            reloadRequest.current = null
        }
    }, [client])

    function saveProbeURL () {
        const normalized = normalizeProbeURL(probeURL)
        if (normalized === undefined) {
            error(t('messages.invalidProbeURL'))
            return
        }
        setSettings('probeURL', normalized)
        setProbeURL(normalized)
        success(t('messages.probeURLSaved'))
    }

    function resetProbeURL () {
        setSettings('probeURL', DEFAULT_PROBE_URL)
        setProbeURL(DEFAULT_PROBE_URL)
    }

    async function handleReloadConfig () {
        if (reloadRequest.current) return
        const request = new AbortController()
        reloadRequest.current = request
        setReloading(true)
        try {
            await client.reloadConfig(request.signal)
            if (request.signal.aborted) return
            success(t('messages.reloadOk'))
            // Reload already succeeded; a failed read must not report that the
            // configuration was rejected. SWR keeps its previous snapshot.
            void fetchGeneral().catch(() => undefined)
        } catch (reason) {
            if (request.signal.aborted) return
            const detail = axios.isAxiosError<{ message?: string }>(reason)
                ? reason.response?.data?.message ?? reason.message
                : reason instanceof Error ? reason.message : ''
            error(`${t('messages.reloadErr')}${detail ? ': ' + String(detail).slice(0, 300) : ''}`)
        } finally {
            if (reloadRequest.current === request) {
                reloadRequest.current = null
                setReloading(false)
            }
        }
    }

    async function changeRuntimeConfig (patch: Partial<Config>) {
        try {
            await client.updateConfig(patch)
            await fetchGeneral()
        } catch (reason) {
            error(isDesktop ? desktopError(reason).message : td('operationFailed'))
        }
    }

    async function handleProxyModeChange (mode: string) {
        await changeRuntimeConfig({ mode })
    }

    async function handleStartAtLoginChange (state: boolean) {
        if (isDesktop) {
            try { await desktopAction('desktop_set_autostart', { enabled: state }) } catch (reason) { error(desktopError(reason).message) }
            return
        }
        await jsBridge?.setStartAtLogin(state)
        await fetchClashXData()
    }

    async function handleSetSystemProxy (state: boolean) {
        if (isDesktop) {
            try { await desktopAction('desktop_set_system_proxy', { enabled: state }) } catch (reason) { error(desktopError(reason).message) }
            return
        }
        await jsBridge?.setSystemProxy(state)
        await fetchClashXData()
    }

    function changeLanguage (language: Lang) {
        setLang(language)
    }

    async function handleHttpPortSave () {
        await changeRuntimeConfig({ port: info.httpProxyPort })
    }

    async function handleSocksPortSave () {
        await changeRuntimeConfig({ 'socks-port': info.socks5ProxyPort })
    }

    async function handleMixedPortSave () {
        await changeRuntimeConfig({ 'mixed-port': info.mixedProxyPort })
    }

    async function handleAllowLanChange (state: boolean) {
        await changeRuntimeConfig({ 'allow-lan': state })
    }

    const {
        hostname: externalControllerHost,
        port: externalControllerPort,
    } = apiInfo

    const { allowLan, mode } = general

    const startAtLogin = isDesktop ? desktop.status?.autostart ?? false : clashXData?.startAtLogin ?? false
    const systemProxy = isDesktop ? desktop.status?.systemProxy ?? false : clashXData?.systemProxy ?? false
    const isClashX = clashXData?.isClashX ?? false

    const proxyModeOptions = useMemo(() => {
        const options = [
            { label: t('values.global'), value: 'global' },
            { label: t('values.rules'), value: 'rule' },
            { label: t('values.direct'), value: 'direct' },
        ] as Array<{ label: string, value: string }>
        if (premium && !isDesktop) {
            options.push({ label: t('values.script'), value: 'script' })
        }
        return options
    }, [t, premium])

    const controllerOptions = hostsStorage.map(
        (h, idx) => ({ value: idx, label: <span className="settings-endpoint">{h.hostname}:{h.port}</span> }),
    )

    const controllers = isDesktop || isClashX || controllerFromURL
        ? <div className="settings-controller-fixed">
            <span className="controller-endpoint">{isDesktop ? td('managedController') : `${apiInfo.protocol}//${externalControllerHost}:${externalControllerPort}`}</span>
            {!isDesktop && !isClashX && controllerFromURL && <p className="settings-hint">{t('controllerFromURL')}</p>}
        </div>
        : (
            <>
                <Select
                    className="settings-controller-select"
                    ariaLabel={t('labels.externalController')}
                    disabled={hostsStorage.length < 2 && !isClashX}
                    options={controllerOptions}
                    value={hostSelectIdx}
                    onSelect={idx => setHostSelectIdx(idx)}
                />
                <button
                    type="button"
                    className={classnames({ 'modify-btn': !isClashX }, 'external-controller')}
                    onClick={() => !isClashX && setIdentity(false)}>
                    {t('labels.edit')}
                </button>
            </>
        )

    return (
        <div className="page settings-page">
            <Header title={t('title')} />

            {isDesktop && <DesktopControls onEditConfig={openConfigEditor} />}

            <section className="settings-section">
                <h2 className="settings-section-title">{t('sections.proxy')}</h2>
                <Card className="settings-card">
                    <div className="settings-row">
                        <span className="label">{t('labels.proxyMode')}</span>
                        <ButtonSelect
                            label={t('labels.proxyMode')}
                            disabled={coreOffline}
                            options={proxyModeOptions}
                            value={mode}
                            onSelect={handleProxyModeChange}
                        />
                    </div>
                    <div className="settings-row">
                        <span className="label">{t('labels.allowConnectFromLan')}</span>
                        <Switch label={t('labels.allowConnectFromLan')} disabled={coreOffline} checked={allowLan} onChange={handleAllowLanChange} />
                    </div>
                    <div className="settings-row">
                        <label className="label" htmlFor="settings-http-port">{t('labels.httpProxyPort')}</label>
                        <Input
                            id="settings-http-port"
                            className="settings-port"
                            align="right"
                            mono
                            inputMode="numeric"
                            disabled={isClashX || coreOffline}
                            value={info.httpProxyPort}
                            onChange={httpProxyPort => set('httpProxyPort', +httpProxyPort)}
                            onBlur={handleHttpPortSave}
                        />
                    </div>
                    <div className="settings-row">
                        <label className="label" htmlFor="settings-socks-port">{t('labels.socks5ProxyPort')}</label>
                        <Input
                            id="settings-socks-port"
                            className="settings-port"
                            align="right"
                            mono
                            inputMode="numeric"
                            disabled={isClashX || coreOffline}
                            value={info.socks5ProxyPort}
                            onChange={socks5ProxyPort => set('socks5ProxyPort', +socks5ProxyPort)}
                            onBlur={handleSocksPortSave}
                        />
                    </div>
                    <div className="settings-row">
                        <label className="label" htmlFor="settings-mixed-port">{t('labels.mixedProxyPort')}</label>
                        <Input
                            id="settings-mixed-port"
                            className="settings-port"
                            align="right"
                            mono
                            inputMode="numeric"
                            disabled={isClashX || coreOffline}
                            value={info.mixedProxyPort}
                            onChange={mixedProxyPort => set('mixedProxyPort', +mixedProxyPort)}
                            onBlur={handleMixedPortSave}
                        />
                    </div>
                </Card>
            </section>

            <section className="settings-section">
                <h2 className="settings-section-title">{t('sections.controller')}</h2>
                <Card className="settings-card">
                    <div className="settings-row settings-row-wrap">
                        <span className="label">{t('labels.externalController')}</span>
                        <div className="settings-controller">
                            { controllers }
                        </div>
                    </div>
                    <div className="settings-row settings-row-action">
                        <Button icon="edit" disabled={isClashX || reloading} onClick={openConfigEditor}>{t('labels.editConfig')}</Button>
                        <Button icon="update" loading={reloading} disabled={reloading} onClick={handleReloadConfig}>{t(reloading ? 'labels.reloading' : 'labels.reloadConfig')}</Button>
                    </div>
                </Card>
            </section>

            <section className="settings-section">
                <h2 className="settings-section-title">{t('sections.probe')}</h2>
                <Card className="settings-card">
                    <form className="settings-probe-url" onSubmit={event => { event.preventDefault(); saveProbeURL() }}>
                        <label className="label" htmlFor="probe-url">{t('labels.probeURL')}</label>
                        <div className="settings-probe-field">
                            <input
                                id="probe-url"
                                className="input mono"
                                value={probeURL}
                                onChange={event => setProbeURL(event.target.value)}
                                type="url"
                                inputMode="url"
                                spellCheck={false}
                                autoCapitalize="off"
                                maxLength={2048}
                                disabled={isClashX}
                                aria-describedby="probe-url-hint" />
                            <div className="settings-probe-actions">
                                <button className="button button-primary" type="submit" disabled={isClashX}>{t('labels.save')}</button>
                                <button className="button button-normal" type="button" disabled={isClashX} onClick={resetProbeURL}>{t('labels.resetProbeURL')}</button>
                            </div>
                        </div>
                        <p id="probe-url-hint" className="settings-hint">{t('probeURLHint')}</p>
                    </form>
                </Card>
            </section>

            <section className="settings-section">
                <h2 className="settings-section-title">{t('sections.system')}</h2>
                <Card className="settings-card">
                    <div className="settings-row">
                        <span className="label">{t('labels.language')}</span>
                        <ButtonSelect label={t('labels.language')} options={languageOptions} value={lang} onSelect={(lang) => changeLanguage(lang as Lang)} />
                    </div>
                    <div className="settings-row">
                        <span className="label">{t('labels.startAtLogin')}</span>
                        <Switch label={t('labels.startAtLogin')} disabled={isDesktop ? !!desktop.busy || !desktop.status?.capabilities.autostart : !clashXData?.isClashX} checked={startAtLogin} onChange={handleStartAtLoginChange} />
                    </div>
                    <div className="settings-row">
                        <span className="label">{t('labels.setAsSystemProxy')}</span>
                        <Switch
                            label={t('labels.setAsSystemProxy')}
                            disabled={isDesktop ? !!desktop.busy || !desktop.status?.capabilities.systemProxy : !isClashX}
                            checked={systemProxy}
                            onChange={handleSetSystemProxy}
                        />
                    </div>
                </Card>
            </section>

            <section className="settings-section">
                <h2 className="settings-section-title">{t('sections.update')}</h2>
                {isDesktop && <DesktopAppUpdate />}
                {(!isDesktop || desktop.status?.phase === 'running') && <UpdateCheck client={client} kernelVersion={version} resourcesOnly={isDesktop} />}
            </section>
            {!isDesktop && <ConfigEditor show={editorOpen} onClose={() => setEditorOpen(false)} />}
        </div>
    )
}
