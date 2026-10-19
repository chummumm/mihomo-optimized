# Code signing policy — draft, not yet enabled

Project: [Mihomo Optimized](https://github.com/chummumm/mihomo-optimized)

Prepared: 2026-10-19

## Current status

This is a proposed policy for a SignPath Foundation application. **No application submission, Foundation acceptance, certificate availability, or completed MFA verification is asserted by this document.** Production SignPath signing is not enabled by publishing this draft.

The proposed desktop repair release is [`mihomo-desktop-v0.1.1`](https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.1.1). It has not yet been built or inspected for signatures. Its publication status must be verified before the link is used as application evidence.

Static inspection of the earlier desktop **0.1.0 samples** found empty PE certificate tables in the installer and the three project-built executables inside its portable ZIP. Their SHA256 manifest establishes file identity, not a Windows publisher signature. This earlier check does not establish the signature status of the future 0.1.1 artifacts.

After acceptance and actual use of Foundation signing, the live policy and release pages will carry this attribution:

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

The attribution above describes the intended future arrangement. It must not be presented as an existing sponsorship or signature while the application is pending.

## Proposed responsibilities and release controls

| Responsibility | Proposed accountable member |
| --- | --- |
| Authors / committers | [chummumm](https://github.com/chummumm) |
| Reviewers of external contributions and upstream changes | [chummumm](https://github.com/chummumm) |
| Release signing approver | [chummumm](https://github.com/chummumm) |

The account holder must confirm these assignments. GitHub Actions may submit a signing request; it does not replace the human approver.

Before production signing, members with repository or signing authority must use MFA. External changes, upstream changes and build scripts require review. Each release-signing request requires human approval and verifiable CI origin. These controls will be configured against the approved repository, release references and artifact scope.

Signing metadata will identify the approved project name and a consistent desktop product version for the build. The component's own core version remains separately documented. Missing executable version resources must be addressed before the executable is submitted under metadata restrictions.

## Source and artifact scope

The native host and service are maintained in this repository alongside the dashboard source. The embedded dashboard derives from Clash Dashboard. The separately built Go core is a maintained derivative of [MetaCubeX/mihomo](https://github.com/MetaCubeX/mihomo), pinned by `desktop/core.json`. The repository's provenance records and component licenses apply to the resulting package.

The proposed signing scope covers the Windows installer, `mihomo-optimized.exe`, `mihomo-desktop-service.exe` and `mihomo-core.exe`, subject to the Foundation's explicit decision. In particular, the modified core and the embedded dashboard lineage must be disclosed. The [application draft](signpath-application.md#evidence-about-the-upstream-windows-binary) records the exact unsigned upstream sample that was inspected; it makes no claim about all upstream releases.

`mihomo-update-verify.exe` is the official GitHub CLI redistributed under a different filename for a fixed verification role. It retains its upstream provenance and is outside the proposed project-signing set. Microsoft WebView2 and other third-party runtime components retain their respective upstream identity and terms.

The release manifest must describe the actual signed and unsigned files. No artifact-selection change may hide a component from Foundation review. Publishing a signed installer must not be described as proof that every contained executable has an individual signature.

## Privacy

The application processes networking data locally to provide the proxy and DNS functions selected by the user. Its maintainers do not operate an application telemetry collection service. Configuration files, preferences and local core logs remain on the user's computer unless the user deliberately shares them. Core logs can contain connection or DNS information; users should inspect logs before publishing them in an issue.

Network access follows the current configuration and the operations the user enables:

- Proxy traffic goes to the destinations, proxy servers and DNS resolvers used by that configuration.
- Importing a configuration URL contacts the selected subscription or configuration provider. Provider refreshes and rule/GeoData downloads follow the loaded configuration.
- The initial sample configuration names Cloudflare `1.1.1.1` and Google `8.8.8.8` as DNS resolvers. Users can edit these values before starting the core.
- Proxy delay and health checks can contact configured probe endpoints. The dashboard's default probe URL is `https://www.gstatic.com/generate_204`.
- Checking or installing desktop updates contacts GitHub's API and release distribution hosts. This update process does not require a user's GitHub account. User-selected network services may receive ordinary network request information, including the connecting IP address.
- The local dashboard communicates with the managed core over an authenticated loopback controller. The desktop controller secret is not an update-server credential.

Third-party services and runtimes have their own policies:

- [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)
- [Microsoft Privacy Statement](https://www.microsoft.com/en-us/privacy/privacystatement), including the Windows and WebView2 runtime environment
- [Cloudflare public DNS resolver privacy](https://developers.cloudflare.com/1.1.1.1/privacy/public-dns-resolver/)
- [Google Public DNS privacy](https://developers.google.com/speed/public-dns/privacy)
- [Google Privacy Policy](https://policies.google.com/privacy), covering the default dashboard probe endpoint

Users should also consult the policies of any proxy, subscription, DNS or rule provider they configure. The project cannot promise that an independently supplied runtime or provider follows the project's own data-handling practices.

## System changes and removal

The application exposes system-proxy and autostart controls. Enabling its TUN service requires an explicit administrator-authorized installation action and creates a TCP inbound firewall allowance scoped to the fixed protected core executable. The service is restricted to managing that core. Stopping or exiting restores the proxy state owned by the application while preserving a later change made by another program.

The settings page can uninstall the TUN service and its owned firewall rules. The installed application includes the normal Windows uninstallation mechanism. User configuration and logs may remain so that an upgrade or reinstall can preserve them; users can remove those local data after closing the application if they no longer need them.

## Publication and incident handling

Once reviewed and adopted, this policy will be linked as **Code signing policy** from the project home page and desktop release pages. A signed release will identify its source commits, artifact checksums and actual signing status. The project will cooperate with the Foundation on reported policy or signing incidents.

Review basis: [SignPath Foundation conditions](https://signpath.org/terms.html) and [SignPath project configuration](https://docs.signpath.io/projects). The Foundation's acceptance and configured signing policy determine the permitted production scope; this draft does not grant that permission.
