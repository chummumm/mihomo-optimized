import { useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'

import { Button, ButtonSelect, Card, Icon, Input, Modal, Switch, error as toastError, success } from '@components'
import { MAX_CONFIG_FILE_BYTES, configFits } from '@containers/Settings/config-editor-model'
import { ConfigEditor } from '@containers/Settings/ConfigEditor'
import { useI18n } from '@stores'

import { cancelDesktopExit, checkDesktopUpdate, confirmDesktopExit, desktopAction, desktopInvoke, desktopSnapshot, dismissDesktopUpdate, installDesktopUpdate, refreshDesktop, setDesktopDialog, useDesktop, useDesktopDirty } from './desktop'
import { desktopError, type DesktopConfigDocument } from './model'
import './desktop.css'

function runAction (command: string, args?: Record<string, unknown>) {
    void desktopAction(command, args).catch(reason => { toastError(desktopError(reason).message) })
}

export function DesktopStatusBar () {
    const desktop = useDesktop()
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const navigate = useNavigate()
    const phase = desktop.status?.phase
    const busy = !!desktop.busy || phase === 'starting' || phase === 'stopping'
    return <div className="desktop-statusbar" aria-label={t('title')}>
        <span className={`desktop-phase is-${phase ?? 'starting'}`} role="status">
            <i aria-hidden="true" />{t('local')} · {phase ? t(`phases.${phase}`) : t('loading')}
        </span>
        <span className="desktop-status-details">
            <span>{t('systemProxy')} <b>{t(desktop.status?.systemProxy ? 'on' : 'off')}</b></span>
            <span>{t('tun')} <b>{t(phase === 'running' && desktop.status?.tun ? 'on' : 'off')}</b></span>
        </span>
        <div className="desktop-status-actions">
            <Button size="sm" icon={phase === 'running' ? 'pause' : 'play'} disabled={busy || !desktop.status} onClick={() => runAction(phase === 'running' ? 'desktop_stop' : 'desktop_start')}>{t(phase === 'running' ? 'stop' : 'start')}</Button>
            <Button size="sm" icon="nav-settings" ariaLabel={t('settings')} onClick={() => navigate('/settings')} />
        </div>
    </div>
}

export function DesktopUpdateNotice () {
    const desktop = useDesktop()
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const navigate = useNavigate()
    const update = desktop.update
    if (!update?.available || !update.version || update.version === desktop.updateDismissedVersion) return null
    return <div className="desktop-update-notice" role="status" aria-live="polite">
        <span className="desktop-update-notice-text"><Icon type="update" size={16} />{t('updateAvailable')} · <b>{update.version}</b></span>
        <div className="desktop-update-notice-actions">
            <Button size="sm" onClick={() => {
                dismissDesktopUpdate()
                navigate({ pathname: '/settings', hash: '#desktop-app-update' })
            }}>{t('viewUpdate')}</Button>
            <Button size="sm" icon="close" ariaLabel={t('dismissUpdate')} onClick={dismissDesktopUpdate} />
        </div>
    </div>
}

export function DesktopUnavailable () {
    const desktop = useDesktop()
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const navigate = useNavigate()
    const phase = desktop.status?.phase
    const busy = !!desktop.busy || phase === 'starting' || phase === 'stopping'
    return <div className="page desktop-recovery-page">
        <Card className="desktop-recovery">
            <div className="desktop-recovery-symbol"><Icon type={desktop.error || phase === 'error' ? 'warning' : 'nav-proxies'} size={36} /></div>
            <h1>{!desktop.ready ? t('loading') : t('notRunning')}</h1>
            <p>{t('notRunningHint')}</p>
            {(desktop.status?.lastError || desktop.error) && <p className="desktop-error" role="alert">{desktop.status?.lastError || (desktop.error?.code === 'desktop_unavailable' ? t('connectionError') : desktop.error?.message)}</p>}
            <div className="desktop-recovery-actions">
                {desktop.status && <Button type="primary" icon="play" loading={busy} disabled={busy} onClick={() => runAction('desktop_start')}>{t('start')}</Button>}
                {!desktop.status && <Button icon="update" onClick={() => { void refreshDesktop() }}>{t('retry')}</Button>}
                <Button icon="nav-settings" onClick={() => navigate('/settings')}>{t('settings')}</Button>
            </div>
        </Card>
    </div>
}

export function DesktopExitGuard () {
    const desktop = useDesktop()
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const [exiting, setExiting] = useState(false)
    const keepEditing = useRef<HTMLButtonElement>(null)
    useEffect(() => {
        if (desktop.exitRequested) keepEditing.current?.focus()
    }, [desktop.exitRequested])
    if (!desktop.exitRequested) return null
    return <Modal show title={t('exitTitle')} footer={false} onClose={() => { if (!exiting) cancelDesktopExit() }}>
        <div className="desktop-exit-dialog" role="alertdialog" aria-label={t('exitTitle')}>
            <p>{t('exitHint')}</p>
            {desktop.writing && <p role="status">{t('waitForSave')}</p>}
            <div className="desktop-dialog-actions">
                <button ref={keepEditing} type="button" className="button button-normal button-md" disabled={exiting} onClick={cancelDesktopExit}>{t('exitCancel')}</button>
                <Button type="danger" icon="close" disabled={exiting || desktop.writing} loading={exiting} onClick={() => {
                    setExiting(true)
                    void confirmDesktopExit().catch(reason => { toastError(desktopError(reason).message); setExiting(false) })
                }}>{t('exitDiscard')}</Button>
            </div>
        </div>
    </Modal>
}

// Keep native source-file sessions outside the route outlet. Browser-back and
// core recovery navigation must not discard an open draft or in-flight write.
export function DesktopDialogs () {
    const { dialog } = useDesktop()
    return <>
        <ConfigEditor show={dialog === 'config'} onClose={() => setDesktopDialog(null)} />
        {dialog === 'import' && <DesktopImportDialog onClose={() => setDesktopDialog(null)} />}
    </>
}

export function DesktopControls ({ onEditConfig }: { onEditConfig: () => void }) {
    const desktop = useDesktop()
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const { t: settings } = translation('Settings')
    const status = desktop.status
    const busy = !!desktop.busy || status?.phase === 'starting' || status?.phase === 'stopping'
    return <section className="settings-section desktop-controls">
        <h2 className="settings-section-title">{t('title')}</h2>
        <Card className="settings-card">
            <div className="settings-row">
                <span className="label">{t('local')}</span>
                <span className={`desktop-phase is-${status?.phase ?? 'error'}`}><i aria-hidden="true" />{status ? t(`phases.${status.phase}`) : t('connectionError')}</span>
            </div>
            <div className="settings-row settings-row-action">
                <Button icon="update" disabled={busy} onClick={() => { void refreshDesktop() }}>{t('retry')}</Button>
                <Button icon="play" type="primary" disabled={busy || !status || status.phase === 'running'} loading={desktop.busy === 'desktop_start'} onClick={() => runAction('desktop_start')}>{t('start')}</Button>
                <Button icon="pause" disabled={busy || status?.phase !== 'running'} loading={desktop.busy === 'desktop_stop'} onClick={() => runAction('desktop_stop')}>{t('stop')}</Button>
                <Button icon="update" disabled={busy || !status} loading={desktop.busy === 'desktop_restart'} onClick={() => runAction('desktop_restart')}>{t('restart')}</Button>
            </div>
            {(status?.lastError || desktop.error) && <div className="desktop-error desktop-inset" role="alert">{status?.lastError || (desktop.error?.code === 'desktop_unavailable' ? t('connectionError') : desktop.error?.message)}</div>}
            <div className="settings-row">
                <div><span className="label">{t('tun')}</span><p className="settings-hint">{t('tunHint')}</p></div>
                <Switch label={t('tun')} checked={status?.tun ?? false} disabled={busy || !status?.capabilities.tun} onChange={enabled => runAction('desktop_set_tun', { enabled })} />
            </div>
            <div className="settings-row">
                <span className="label">{t('service')}</span>
                <span className="desktop-service-status">{status?.service?.running ? t('serviceRunning') : t(status?.service?.installed ? 'serviceInstalled' : 'serviceMissing')}</span>
                <Button size="sm" icon={status?.service?.installed ? 'trash' : 'arrow-down'} disabled={busy || !status?.capabilities.serviceManagement || status.tun} onClick={() => runAction(status?.service?.installed ? 'desktop_uninstall_service' : 'desktop_install_service')}>{t(status?.service?.installed ? 'uninstallService' : 'installService')}</Button>
            </div>
            <div className="settings-row desktop-config-path">
                <span className="label">{t('config')}</span>
                <span className="reading" title={status?.configPath}>{status?.configPath || '—'}</span>
            </div>
            <div className="settings-row settings-row-action">
                <Button icon="file" disabled={!status} onClick={() => runAction('desktop_open_directory', { kind: 'config' })}>{t('configFolder')}</Button>
                <Button icon="nav-logs" disabled={!status} onClick={() => runAction('desktop_open_directory', { kind: 'logs' })}>{t('logFolder')}</Button>
                <Button icon="edit" disabled={!status} onClick={onEditConfig}>{settings('labels.editConfig')}</Button>
                <Button icon="arrow-down" disabled={busy || !status?.capabilities.configImport} onClick={() => setDesktopDialog('import')}>{t('importConfig')}</Button>
            </div>
        </Card>
    </section>
}

function DesktopImportDialog ({ onClose }: { onClose: () => void }) {
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const [mode, setMode] = useState<'file' | 'text' | 'url'>('file')
    const [content, setContent] = useState('')
    const [url, setURL] = useState('')
    const [filename, setFilename] = useState('')
    const [revision, setRevision] = useState<string>()
    const [problem, setProblem] = useState('')
    const [loading, setLoading] = useState(true)
    const [saving, setSaving] = useState(false)
    const [discard, setDiscard] = useState(false)
    const active = useRef(true)
    const fileRead = useRef(0)
    const fileInput = useRef<HTMLInputElement>(null)
    const dirty = !!content || !!url
    useDesktopDirty('config-import', dirty, saving)

    useEffect(() => {
        active.current = true
        void desktopInvoke<DesktopConfigDocument>('desktop_read_config').then(file => {
            if (active.current) setRevision(file.revision)
        }).catch(reason => {
            if (active.current) setProblem(desktopError(reason).message)
        }).finally(() => { if (active.current) setLoading(false) })
        return () => { active.current = false; fileRead.current++ }
    }, [])

    function close () {
        if (saving) return
        if (dirty) setDiscard(true)
        else onClose()
    }

    async function readFile (file: File | undefined) {
        if (!file) return
        const request = ++fileRead.current
        setProblem('')
        // A new selection must never leave the prior file ready to import.
        setContent('')
        setFilename('')
        if (file.size > MAX_CONFIG_FILE_BYTES) { setProblem(t('importTooLarge')); return }
        try {
            const text = await file.text()
            if (!active.current || request !== fileRead.current) return
            if (!configFits(text, MAX_CONFIG_FILE_BYTES)) { setProblem(t('importTooLarge')); return }
            setContent(text)
            setFilename(file.name)
        } catch { if (active.current && request === fileRead.current) setProblem(t('importReadFailed')) }
    }

    async function save () {
        if (saving || loading || revision === undefined) return
        let parsed: URL | undefined
        if (mode === 'url') {
            try { parsed = new URL(url.trim()) } catch { setProblem(t('importBadURL')); return }
            if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password) { setProblem(t('importBadURL')); return }
        } else {
            if (!content.trim()) { setProblem(t('importEmpty')); return }
            if (!configFits(content, MAX_CONFIG_FILE_BYTES)) { setProblem(t('importTooLarge')); return }
        }
        setSaving(true)
        setProblem('')
        let saved = false
        try {
            const next = await desktopInvoke<DesktopConfigDocument>(mode === 'url' ? 'desktop_import_config_url' : 'desktop_import_config', mode === 'url'
                ? { url: parsed!.href, expectedRevision: revision }
                : { content, expectedRevision: revision })
            saved = true
            if (active.current) { setRevision(next.revision); setContent(''); setURL('') }
            await desktopInvoke('desktop_reload_config', { force: true })
            await refreshDesktop()
            if (!active.current) return
            success(t(desktopSnapshot().status?.phase === 'running' ? 'configSaved' : 'configSavedStopped'))
            onClose()
        } catch (reason) {
            if (!active.current) return
            const failure = desktopError(reason)
            setProblem(saved ? t('configReloadFailed') : failure.code === 'config_conflict' ? t('configConflict') : failure.message)
        } finally { if (active.current) setSaving(false) }
    }

    return <Modal show title={t('importTitle')} size="big" footer={false} onClose={close} className="desktop-import-modal">
        <div className="desktop-import-content">
            <p className="settings-hint">{t('importHint')}</p>
            <ButtonSelect label={t('importTitle')} value={mode} disabled={saving} options={[
                { label: t('importFile'), value: 'file' }, { label: t('importText'), value: 'text' }, { label: t('importURL'), value: 'url' },
            ]} onSelect={value => { if (!saving && (value === 'file' || value === 'text' || value === 'url')) { fileRead.current++; setMode(value); setProblem('') } }} />
            {mode === 'url'
                ? <div className="desktop-import-url">
                    <label htmlFor="desktop-config-url">{t('importURL')}</label>
                    <Input id="desktop-config-url" inputMode="url" disabled={saving} value={url} onChange={setURL} />
                    <p className="settings-hint">{t('importURLHint')}</p>
                </div>
                : <>
                    {mode === 'file' && <div className="desktop-import-file">
                        <input ref={fileInput} type="file" accept=".yaml,.yml,text/yaml,text/x-yaml,application/yaml" disabled={saving} aria-label={t('importFile')} onChange={event => { void readFile(event.currentTarget.files?.[0]) }} />
                        {filename && <span className="reading">{filename}</span>}
                        <p className="settings-hint">{t('importFileHint')}</p>
                    </div>}
                    <textarea className="desktop-yaml" aria-label={t('importPreview')} placeholder={t('importPlaceholder')} value={content} onChange={event => { if (configFits(event.target.value, MAX_CONFIG_FILE_BYTES)) setContent(event.target.value); else setProblem(t('importTooLarge')) }} disabled={saving} spellCheck={false} autoComplete="off" autoCorrect="off" autoCapitalize="off" wrap="off" />
                </>}
            {loading && <p role="status">{t('configReadFirst')}</p>}
            {problem && <p className="desktop-error" role="alert">{problem}</p>}
            {discard
                ? <div className="desktop-import-discard" role="alertdialog" aria-label={t('importDiscardTitle')}>
                    <b>{t('importDiscardTitle')}</b><p>{t('importDiscardHint')}</p>
                    <div className="desktop-dialog-actions"><Button onClick={() => setDiscard(false)}>{t('keepEditing')}</Button><Button type="danger" onClick={onClose}>{t('discard')}</Button></div>
                </div>
                : <div className="desktop-dialog-actions">
                    <Button disabled={saving} onClick={close}>{t('importCancel')}</Button>
                    <Button type="primary" icon="save" loading={saving} disabled={loading || saving || revision === undefined} onClick={() => { void save() }}>{t(saving ? 'importing' : 'importReplace')}</Button>
                </div>}
        </div>
    </Modal>
}

export function DesktopAppUpdate () {
    const desktop = useDesktop()
    const { translation } = useI18n()
    const { t } = translation('Desktop')
    const location = useLocation()
    const card = useRef<HTMLDivElement>(null)
    const update = desktop.update
    const busy = desktop.updateBusy
    const runtimeBusy = !!desktop.busy || !desktop.status || desktop.writing ||
        desktop.status.phase === 'starting' || desktop.status.phase === 'stopping'
    useEffect(() => {
        if (location.hash !== '#desktop-app-update') return
        card.current?.scrollIntoView({ block: 'start' })
        card.current?.focus({ preventScroll: true })
    }, [location.hash, location.key])
    const progress = busy ? desktop.updateProgress : null
    const ratio = progress?.total && progress.downloaded !== undefined ? Math.min(1, Math.max(0, progress.downloaded / progress.total)) : undefined
    return <Card ref={card} id="desktop-app-update" tabIndex={-1} aria-label={t('appUpdate')} className="settings-card desktop-app-update">
        <div className="settings-row settings-update-row">
            <div className="settings-update-name"><span className="label">{t('appUpdate')}</span><span className="settings-update-version">{update?.currentVersion ?? desktop.status?.appVersion ?? '—'}</span></div>
            <div className="settings-update-result">
                {busy ? <span role="status">{t(`updateStages.${progress?.stage ?? busy}`)}</span> : update && <span>{t(update.available ? 'updateAvailable' : 'updateCurrent')}{update.available ? ` · ${update.version}` : ''}</span>}
            </div>
        </div>
        {ratio !== undefined && <progress className="desktop-download-progress" value={ratio} max={1} aria-label={t('updateStages.downloading')} />}
        <p className="settings-hint desktop-inset">{t('updateHint')}</p>
        {update?.canInstall === false && <p className="settings-hint desktop-inset">{t('portableUpdateHint')}</p>}
        {update?.body && <details className="desktop-update-notes desktop-inset"><summary>{update.version}</summary><pre>{update.body}</pre></details>}
        {desktop.updateError && <p className="desktop-error desktop-inset" role="alert">{desktop.updateError.message}</p>}
        {desktop.dirty && <p className="settings-hint desktop-inset">{t('saveBeforeUpdate')}</p>}
        <div className="settings-row settings-row-action">
            <Button icon="update" loading={busy === 'checking'} disabled={!!busy} onClick={() => { void checkDesktopUpdate() }}>{t('checkUpdate')}</Button>
            {update?.available && <Button type="primary" icon="arrow-down" loading={busy === 'installing'} disabled={!!busy || runtimeBusy || desktop.dirty || (!update.canInstall && !update.releaseUrl)} onClick={() => { void installDesktopUpdate() }}>{t(update.canInstall ? 'installUpdate' : 'downloadInstaller')}</Button>}
        </div>
    </Card>
}
