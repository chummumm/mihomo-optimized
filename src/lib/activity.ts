// Native hide/minimize does not consistently change WebView visibilityState.
// The desktop bridge sets this attribute and sends the existing visibility event.
export function isPageVisible (page: Document = document): boolean {
    return page.visibilityState !== 'hidden' && !page.hidden &&
        page.documentElement?.getAttribute('data-desktop-hidden') !== 'true'
}
