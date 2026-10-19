import type { Snapshot } from './request'
import { StreamReader } from './streamer'
import { isPageVisible } from './activity'

export interface ConnectionSnapshot extends Snapshot {
    stream: {
        generation: number
        receivedAt: number
    }
}

// Every accepted snapshot is delivered, including connections that open and
// close between display updates. Consumers may defer publishing their view, but
// must ingest each snapshot if they retain observed connection history.
export class ConnectionStreamReader extends StreamReader<ConnectionSnapshot> {
    private generation = 0
    private page: { document: Document, window: Window } | undefined
    private pageURL = ''
    private enabled = false
    private pageHidden = false
    private frozen = false

    constructor () {
        super({ bufferLength: 0 })
    }

    protected override resetSource () {
        this.generation++
        // The data envelope also carries the generation: a newly subscribed
        // consumer cannot miss the first baseline reset of an existing stream.
        this.EE.emit('reset')
    }

    protected override parseMessage (raw: string): ConnectionSnapshot {
        const receivedAt = performance.now()
        const snapshot: Snapshot = JSON.parse(raw)
        if (snapshot === null || typeof snapshot !== 'object' ||
            !Number.isFinite(snapshot.uploadTotal) || snapshot.uploadTotal < 0 ||
            !Number.isFinite(snapshot.downloadTotal) || snapshot.downloadTotal < 0 ||
            (snapshot.connections !== null && !Array.isArray(snapshot.connections))) {
            throw new TypeError('Invalid connection snapshot')
        }
        return { ...snapshot, stream: { generation: this.generation, receivedAt } }
    }

    private refreshPage = () => {
        const active = this.enabled && !this.pageHidden && !this.frozen &&
            this.page !== undefined && isPageVisible(this.page.document)
        const url = active ? this.pageURL : ''
        if (!url && !this.url) return
        super.connect(url)
    }

    private freeze = () => {
        this.frozen = true
        this.refreshPage()
    }

    private resume = () => {
        this.frozen = false
        this.refreshPage()
    }

    private pagehide = () => {
        this.pageHidden = true
        this.refreshPage()
    }

    private pageshow = () => {
        this.pageHidden = false
        this.frozen = false
        this.refreshPage()
    }

    // Remains subscribed while paused. Visibility, page lifecycle and the
    // caller's enabled state all have to permit a fresh statistics socket.
    connectWhenVisible (url: string, enabled = true) {
        this.pageURL = url
        this.enabled = enabled
        if (this.page === undefined) {
            this.page = { document, window }
            document.addEventListener('visibilitychange', this.refreshPage)
            document.addEventListener('freeze', this.freeze)
            document.addEventListener('resume', this.resume)
            window.addEventListener('pagehide', this.pagehide)
            window.addEventListener('pageshow', this.pageshow)
        }
        this.refreshPage()
    }

    override destory () {
        if (this.page !== undefined) {
            this.page.document.removeEventListener('visibilitychange', this.refreshPage)
            this.page.document.removeEventListener('freeze', this.freeze)
            this.page.document.removeEventListener('resume', this.resume)
            this.page.window.removeEventListener('pagehide', this.pagehide)
            this.page.window.removeEventListener('pageshow', this.pageshow)
        }
        this.page = undefined
        this.pageURL = ''
        this.enabled = false
        this.pageHidden = false
        this.frozen = false
        super.destory()
    }
}
