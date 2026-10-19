import { isClashX, setupJsBridge } from '@lib/jsBridge'

import renderApp from './render'
import { initializeDesktop, isDesktop } from './platform/desktop'

/**
 * Global entry
 * Will check if need setup jsbridge
 */
if (isDesktop()) {
    // Show recovery controls immediately; startup IPC must not leave a blank UI.
    renderApp()
    void initializeDesktop()
} else if (isClashX()) {
    setupJsBridge(() => renderApp())
} else {
    renderApp()
}
