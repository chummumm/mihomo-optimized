# SignPath Foundation application draft

Prepared on 2026-10-19 for the maintainer of `chummumm/mihomo-optimized`.

**Status: preparation only.** This document does not establish that an application has been submitted, accepted, or that a certificate has been made available. GitHub and SignPath MFA have not been verified. Desktop 0.1.1 is publicly released; its verified release evidence is recorded below. Personal contact details and unconfirmed names are deliberately excluded from this public document.

Static inspection of the published desktop 0.1.1 installer and the three project-built executables inside its portable ZIP found no embedded Authenticode certificate tables. The exact assets, hashes, build and inspection scope are recorded in [Evidence about the desktop release](#evidence-about-the-desktop-release). This release does not use SignPath signing; an unsigned public release does not establish Foundation acceptance.

## Official application and references

- [Application page](https://signpath.org/apply.html)
- [Foundation conditions](https://signpath.org/terms.html), currently labelled a draft Code of Conduct on the official site
- [SignPath project and signing-policy configuration](https://docs.signpath.io/projects)
- [Companion policy draft](code-signing-policy.draft.md)

The current application is an embedded HubSpot EU form. Its public identifiers are portal `145110231`, form `bf62807d-bb72-4e45-9bde-1f3a53ba2472`, region `eu1`. The fields below were read from the [public render definition](https://forms-eu1.hsforms.com/embed/v4/render-definition/145110231/bf62807d-bb72-4e45-9bde-1f3a53ba2472), reached through the official page's embed configuration, and checked against the rendered form. The rendered form also requires consent to storing and processing personal data. These read-only checks did not submit an application.

## Eligibility and source disclosure

Foundation review is required. Its current conditions cover existing releases, active open-source maintenance, named responsibilities, MFA, and human release approval. Modified upstream binaries have additional requirements concerning signed upstream builds, visible derivation and release-branch ancestry. The Foundation determines whether a particular project and signing scope qualify.

### Project identity

- Repository: <https://github.com/chummumm/mihomo-optimized>
- Repository owner: [chummumm](https://github.com/chummumm); the connected repository metadata reports administrator access.
- GitHub repository ID: `1410715865`.
- The public repository was created on 2026-10-08. This is an early-stage independent project; it must not present the upstream project's adoption as its own.
- GitHub's repository API reports `fork: false`. The main README explicitly identifies the project as an independent derivative of [MetaCubeX/mihomo](https://github.com/MetaCubeX/mihomo), and the repository preserves upstream history and the `UPSTREAM_VERSION` / `UPSTREAM_COMMIT` records. GitHub's fork flag alone does not establish the Foundation's decision about visible derivation.
- The Go core is maintained under the repository's GPL-3.0 license. The dashboard/desktop source branch carries the MIT license inherited from Clash Dashboard. Component licenses remain applicable to the distributed package; this application must not describe the whole package as exclusively MIT-licensed.

### Requested review scope

The intended Windows distribution contains an NSIS installer and a portable ZIP. Please ask the Foundation to decide the permitted scope explicitly:

| Artifact | Source and requested treatment |
| --- | --- |
| `mihomo-optimized.exe` | The project's Rust desktop host, embedding its maintained dashboard derived from Clash Dashboard. Request review of this source and embedded asset lineage. |
| `mihomo-desktop-service.exe` | The project's Rust helper and restricted Windows service. Request signing of the repository-built executable. |
| `mihomo-core.exe` | A modified build of MetaCubeX/mihomo. Request an explicit eligibility decision for the modified upstream binary. |
| `mihomo-desktop-<version>-windows-x64-setup.exe` | The repository-built NSIS installer and its documented payload. Request review of both the installer and the included components. |
| `mihomo-update-verify.exe` | A renamed, otherwise unchanged official GitHub CLI binary used for update verification. Disclose its upstream provenance and exclude it from signing with this project's certificate. |
| Microsoft WebView2 and other upstream runtime components | Retain their respective upstream identity, distribution terms and signatures. Do not represent them as original project code. |

The desktop build pins the core source commit in `desktop/core.json`; `build-info.json` records the UI/desktop source and core commit used for a package. Core and dashboard/desktop source live in different branches of the same repository. This arrangement should be included in the Foundation's origin-verification review.

### Evidence about the desktop release

The public [desktop 0.1.1 release](https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.1.1), release ID `416995836`, was published at `2026-10-19T07:02:13Z`. The release metadata checked on 2026-10-19 reports `draft: false` and `prerelease: false`.

- Tag and desktop/UI source: [`e2597a790cccb807de2b1ac58d3ba62346f94f7b`](https://github.com/chummumm/mihomo-optimized/tree/e2597a790cccb807de2b1ac58d3ba62346f94f7b).
- Pinned core source: [`e16270b1a00b3ea3a6b999760655f4299c4ffcdb`](https://github.com/chummumm/mihomo-optimized/tree/e16270b1a00b3ea3a6b999760655f4299c4ffcdb).
- Successful production build and release: [Windows desktop run `39212119028`](https://github.com/chummumm/mihomo-optimized/actions/runs/39212119028). It passed 10 native tests, 6 desktop-managed core tests and 11 installed GUI/service acceptance checks. The GUI checks used medium integrity with the administrator group disabled for allowed access, and exercised the service's allowed identity query and denied mutation rights.
- Published source record: [`build-info.json`](https://github.com/chummumm/mihomo-optimized/releases/download/mihomo-desktop-v0.1.1/build-info.json).

The following files were downloaded from the public release on 2026-10-19. Their SHA256 values matched both GitHub's asset digests and the published [`SHA256SUMS`](https://github.com/chummumm/mihomo-optimized/releases/download/mihomo-desktop-v0.1.1/SHA256SUMS). Portable ZIP integrity and its embedded build-info were also checked against the published source record.

| Public download | Bytes | SHA256 |
| --- | --- | --- |
| [Windows x64 installer](https://github.com/chummumm/mihomo-optimized/releases/download/mihomo-desktop-v0.1.1/mihomo-desktop-0.1.1-windows-x64-setup.exe) | 31676367 | `64326bf2d2672cfc76fbb1c1dbd25138a438ee0e78751ab72ac2a91a62f84690` |
| [Windows x64 portable ZIP](https://github.com/chummumm/mihomo-optimized/releases/download/mihomo-desktop-v0.1.1/mihomo-desktop-0.1.1-windows-x64-portable.zip) | 43224131 | `7a1c33425f0fe118700828eb3749f36ed92d5647fc3afe01128c75817a0a9e07` |

A separate static inspection read the PE `IMAGE_DIRECTORY_ENTRY_SECURITY` entry of the installer and the following files under `Mihomo Optimized/` in that portable ZIP. This inspection did not execute the downloaded programs.

| Inspected project PE file | SHA256 | Certificate table file offset | Certificate table size |
| --- | --- | --- | --- |
| `mihomo-desktop-0.1.1-windows-x64-setup.exe` | `64326bf2d2672cfc76fbb1c1dbd25138a438ee0e78751ab72ac2a91a62f84690` | 0 | 0 |
| `mihomo-optimized.exe` | `9d073fde5724af114555ca517b90556e3cea128f6733de4507e64f04f2e3472e` | 0 | 0 |
| `mihomo-core.exe` | `734c59effb6bc32e12b6a9ee93b2609535340a94229fe57f7b4739f851080148` | 0 | 0 |
| `mihomo-desktop-service.exe` | `10d256961ee55d8cda7641ea2aa0a206c5ea1164a44aa1d700c6117d60f1d7ba` | 0 | 0 |

These results establish that these exact four files have no embedded Authenticode certificate table. They are not a Windows certificate-chain, timestamp or external-catalog signature validation, and do not establish the signature status of third-party components or the installed uninstaller. The release's GitHub/Sigstore build attestation and SHA256 manifest establish different properties from a Windows publisher signature. The SignPath application remains unsubmitted and unapproved, and production SignPath signing remains disabled.

### Evidence about the upstream Windows binary

On 2026-10-19, a read-only check downloaded the following asset from the official [MetaCubeX v1.19.32 release](https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.32):

- Asset: [`mihomo-windows-amd64-v1-v1.19.32.zip`](https://github.com/MetaCubeX/mihomo/releases/download/v1.19.32/mihomo-windows-amd64-v1-v1.19.32.zip)
- Archive SHA256: `00549b347d8e124600e46cac67d59293358454f465e9d86d2511567da56b7029`
- Executable inside the archive: `mihomo-windows-amd64-v1.exe`
- Executable SHA256: `0b54ea7b10e26f6ca628b49e77c13ad9b2587b23393a5b715769cbaeed012af6`
- Its PE `IMAGE_DIRECTORY_ENTRY_SECURITY` file offset and size are both `0`.

This establishes that **this specific upstream executable has no embedded Authenticode certificate table**. It does not establish the signature status of every upstream build, any external catalog, or the Foundation's interpretation of signed upstream releases. No executable was run. The release asset listing checked at that time contained no `.sig`, `.cat`, `.asc` or certificate attachment.

The Foundation should be asked to confirm whether the modified core can be signed and whether the proposed package is acceptable if the permitted executable-signing scope is narrower. Changing an artifact selection must not be used to conceal or bypass this question.

The main README also reproduces an upstream statement concerning downstream use of the name `mihomo`. The maintainer should resolve any naming or affiliation representation before making final declarations. This draft claims no MetaCubeX affiliation or endorsement.

## Form fields and proposed public values

| Field | Required | Proposed value or action |
| --- | --- | --- |
| Project Name | Yes | `Mihomo Optimized` — current product identity; subject to the naming clarification above. |
| Repository URL | Yes | `https://github.com/chummumm/mihomo-optimized` |
| Homepage URL | Yes | `https://github.com/chummumm/mihomo-optimized/tree/clash-dashboard` |
| Download URL | No | `https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.1.1` — public release verified on 2026-10-19; see the desktop release evidence above. |
| Privacy Policy URL | No, but applicable when user data is collected | Publish the reviewed privacy section from the companion policy and use its final public URL. Do not imply that a proposed URL already exists. |
| Wikipedia URL (optional) | No | Leave blank; no article has been verified. |
| Tagline | Yes | Use the text below. |
| Category | Yes | `Other`; the current form has no networking category. |
| Description | Yes | Use the text below, including the derivative disclosure. |
| Reputation | Yes | Use the factual early-stage statement below and verified public links. |
| Maintainer Type | No | `Individual maintainer(s)`, subject to confirmation by the account holder. |
| Build System | Yes | `GitHub Actions` (form value: `GitHub`). |
| First Name | Yes | To be supplied privately by the applicant; do not invent it from a GitHub username. |
| Last Name | Yes | To be supplied privately by the applicant. |
| Email | Yes | The applicant's chosen account and notification email; keep it out of this public preparation document. |
| Company Name | No | Leave blank unless the applicant confirms an applicable organization. |
| Primary Discovery Channel | Yes | The applicant's actual discovery channel. `AI / LLM tools` is available if this conversation was the source. |
| Please specify the exact source (optional) | No | For example, `ChatGPT`, if accurate. |
| Code of Conduct agreement | Yes | The applicant must review the current terms and make the agreement when submitting. This draft records no agreement or submission. |
| Consent to storage and processing of personal data | Yes | The applicant must review and provide this separate consent when submitting; it is not recorded or pre-approved by this draft. |
| Other communications from SignPath | No | Optional marketing consent; leave unchecked unless requested by the applicant. |

### Tagline

An open-source Windows client for configurable proxy and DNS routing, with a local dashboard and an optional managed TUN service.

### Description

Mihomo Optimized provides a desktop interface for managing user-selected proxy configurations, DNS routing and local network activity. The Windows application combines a Rust host and service with our maintained dashboard and a modified Mihomo core. It supports configuration editing, system-proxy controls and optional TUN operation through explicit user actions. The project is independently maintained by chummumm and derives from MetaCubeX/mihomo and Clash Dashboard; it is not an official MetaCubeX distribution. We request review of the Windows installer and project-built executables, with an explicit Foundation decision on the modified upstream core and embedded dashboard lineage. Third-party executables retain their upstream identity.

### Reputation

This is an early-stage independent project. We provide public source history, documented upstream provenance, release records, checksums and automated Windows build and acceptance-test results for review. We do not claim a large independent user base or transfer the upstream project's reputation to this derivative. The Windows tests cover the installed WebView2 interface, configuration recovery, proxy restoration, service lifecycle and isolated TUN traffic. Please evaluate this evidence and advise whether further project history or usage evidence is needed for admission.

Public evidence links; recheck their availability before submission:

- Public source: <https://github.com/chummumm/mihomo-optimized>
- Release history: <https://github.com/chummumm/mihomo-optimized/releases>
- Published desktop 0.1.1 release: <https://github.com/chummumm/mihomo-optimized/releases/tag/mihomo-desktop-v0.1.1>; exact source, successful run and download checks are recorded in [Evidence about the desktop release](#evidence-about-the-desktop-release).
- Desktop build history: <https://github.com/chummumm/mihomo-optimized/actions/workflows/windows-desktop.yml>
- Initial desktop implementation review: <https://github.com/chummumm/mihomo-optimized/pull/49>

### Additional scope explanation for the review

Our Go core contains maintained modifications to MetaCubeX/mihomo. We inspected the official v1.19.32 Windows amd64-v1 executable and found no embedded Authenticode certificate table; the exact artifact and hashes are recorded above. Our repository is an independent GitHub repository with preserved upstream history and explicit README provenance, rather than a GitHub-network fork. Please determine whether this source relationship and upstream signing status satisfy your rules, and which of the requested host, service, modified core and installer artifacts may be signed. We will enable production signing only within the scope you approve.

## Complete option lists observed in the current form

- Category: Accessibility; AI Tools; Audio; Developer Tools; Educational; Gaming; Graphics; Productivity; Science; Other.
- Maintainer Type: Independent community project (no formal organization); Non-profit foundation or research/educational institution; For-profit company or corporate-backed project; Individual maintainer(s); Other (please specify).
- Build System: GitHub Actions (`GitHub`); GitLab CI/CD (`GitLab`).
- Primary Discovery Channel: Organic search; AI / LLM tools; Developer platforms (e.g. GitHub); Community platforms; Social media; Events; Referral; Direct contact; Other.

## Submission handoff

Before submitting, verify the published desktop download and policy links; obtain the applicant's account names and selected email privately; confirm the proposed maintainer roles and MFA setup; and present the current terms for the applicant's agreement. The public repository and release page should link to a section explicitly titled **Code signing policy**.

Keep the application and the first signed release as separate milestones. A submitted form, a successful unsigned build, a SHA256 manifest, or a GitHub/Sigstore build attestation does not mean a Windows Authenticode signature has been issued. Record the Foundation's actual response and approved artifact scope before enabling release signing.
