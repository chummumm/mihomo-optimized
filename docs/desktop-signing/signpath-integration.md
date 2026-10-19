# SignPath 接入准备：Windows 桌面版

状态：**申请准备与设计参考，尚未接入 SignPath，也没有完成 Authenticode 签名验证。** 本文和同目录的 `.yml.example` 不会启用 GitHub Actions 工作流，不会改变现有 Windows 发行或 core 的 latest 标记。

审查基线为桌面合并提交 `ff075b4ab96232cdc409c905a3cda34503e3c439`。该版本的 `desktop/core.json` 固定引用 `e16270b1a00b3ea3a6b999760655f4299c4ffcdb`；本文不要求修改该 Go 核心源码。获批后的实际实现、版本更新和 CI 验证需要另一个可审阅的 PR。

申请材料见 [signpath-application.md](signpath-application.md)，待确认的团队和公开政策见 [code-signing-policy.draft.md](code-signing-policy.draft.md)。官方要求与本文项目实施建议分别标明；申请、邀请、审批成员和凭据仍需真实配置。

## 1. 现有发行与两种签名的职责

现有 `.github/workflows/windows-desktop.yml` 在 `clash-dashboard` 的代码 push 后构建 Windows x64，并在实际安装、WebView2、系统代理、TUN 和退出清理验收通过后发布：

- `mihomo-desktop-<version>-windows-x64-setup.exe`：NSIS 安装版。
- `mihomo-desktop-<version>-windows-x64-portable.zip`：完整运行目录，解压后运行 `mihomo-optimized.exe`。
- `build-info.json`、`SHA256SUMS`、README 和 GitHub 构建证明等附件。

`scripts/desktop-release.py` 使用独立的 `mihomo-desktop-v<version>` 标签，并在创建及发布时传入 `--latest=false`，保留 core 发行的 latest 标记。已发布版本不能覆盖；只能恢复同一个来源提交的未发布草稿。

| 机制 | 验证什么 | 接入 SignPath 后的处理 |
| --- | --- | --- |
| GitHub Actions provenance / Sigstore | 最终文件来自允许的仓库、工作流、分支和源码提交；供现有应用内更新器验证。 | 继续保留，对签名完成后的最终字节重新生成。 |
| Windows Authenticode | PE 文件的发布者签名、证书链、内容完整性和时间戳。 | 由获批的 SignPath 正式政策完成，再在 Windows 上验证。 |
| SHA256SUMS | 下载文件与发布清单是否一致。 | 所有签名、证明和附件生成完毕后重新计算。 |

GitHub provenance 不是 Windows 发布者证书。ZIP 容器也不会因其中一个文件已签名而变成 Windows Authenticode 程序；要验证 ZIP 内实际运行的 EXE。签名并不保证所有 Windows SmartScreen、Smart App Control 或组织策略都立即信任新发行。[1][2]

## 2. 获批前后的外部条件

### SignPath 官方要求

Foundation 开源政策要求项目先获接受，采用受支持的开源许可、有可验证的公开源码与发行，并公开 Code signing policy 和团队角色。所有参与成员需使用 MFA；正式签名需要人工批准。Foundation 对修改上游软件的签署还有可见 fork、上游签名发行等限制。[3]

本仓库含二开 Mihomo 及上游衍生 UI，申请必须如实说明，不得把技术准备、已填写表单或仓库拥有者同意当作 Foundation 已批准。证书由 Foundation 持有，私钥不应导出到 GitHub；CI 使用提交签名请求所需的 API token。[3][4]

获批后在 SignPath 中完成：

1. 确认真实的 organization、project 和正式签名政策。
2. 将 GitHub.com Trusted Build System 加到 organization，并关联到 project。OSS 的来源与受信构建系统验证是必需项。[4][5]
3. 将 project repository 设置为本仓库；将允许的发行分支限制为 `clash-dashboard`，并按 Foundation 批准的策略设置其他来源约束。
4. 配置专用 CI submitter，以及真实的人工 approvers 和 required approvals。官方要求每次正式发行人工批准；多阶段实现产生的每个请求都遵守其所属政策，不能假定一次按钮操作批准所有后续请求。[3][5]
5. 创建并审核 artifact configurations，限制准确文件、签名方式和 PE 产品元数据；明确第三方文件的处理。
6. 如所选政策需要审计日志评价，安装 SignPath GitHub App 并授予指定仓库访问。官方文档将该 App 列为审计日志评价的前提，不能将其误写为所有公开仓库场景的一概前提。[4]

### 需要提供的真实参数

下列名称是本项目建议采用的 GitHub 配置键；**值必须来自实际获批账户，不能从 GitHub 用户名猜测。** 非秘密标识可放 repository/environment variables；token 只能放 secret。

| GitHub 配置键 | SignPath / CI 对应项 | 填写要求 |
| --- | --- | --- |
| `SIGNPATH_ORGANIZATION_ID` | `organization-id` | SignPath organization 的 UUID。 |
| `SIGNPATH_PROJECT_SLUG` | `project-slug` | 实际项目 slug。 |
| `SIGNPATH_RELEASE_POLICY_SLUG` | `signing-policy-slug` | 使用正式证书、来源验证和人工批准的真实政策，不能替换成 test-signing。 |
| `SIGNPATH_RUNTIME_ARTIFACT_CONFIGURATION_SLUG` | 第一阶段 `artifact-configuration-slug` | 对主程序、二开 core、服务逐项限定的配置。 |
| `SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG` | 安装器阶段 `artifact-configuration-slug` | 对最终 setup EXE 限定的配置；不是 NSIS 内部签署已完成的声明。 |
| `SIGNPATH_API_TOKEN`（secret） | `api-token` | 专用 CI 用户的 token；赋予所需提交权限，不使用全局管理员或自动审批 token。 |
| `SIGNPATH_EXPECTED_CERTIFICATE_SHA256` | 本项目回包验证约束 | 获批证书 DER 的 SHA-256 指纹；从可信 SignPath 账户独立取得。证书换发时须审阅更新。 |
| 每次运行的 `desktop_version` | artifact configuration 的版本参数 | 与该次源码、实际 PE 元数据及桌面发行版本一致。该字段不会补写缺失的 PE 元数据。 |
| 每次上传步骤的 `artifact-id` | `github-artifact-id` | 使用本次真实构建上传步骤的输出，不能用手填 ID 冒充构建来源。 |

CI 用户 token 只能在生成时查看，按官方建议保存在 CI secret 中。它用于提交，不代替人的审批。[6]

建议使用专用 GitHub environment 保护签名 secret，并由维护者真实配置可用分支和 reviewers。只在 YAML 写一个 environment 名称不会自动建立所需审批策略；GitHub 的手动触发或 environment 审批也不替代 SignPath 的正式人工批准。

## 3. 文件范围和当前缺口

以下是拟申请的签署范围，是否获准由 Foundation 确认；并非已经签署的文件清单。

| 文件 | 来源 | 拟采取的处理 | 当前尚未完成的工作 |
| --- | --- | --- | --- |
| `mihomo-optimized.exe` | 本仓库的 Rust/Tauri 主程序与内嵌 UI | 用本项目获批政策签署。 | 检查实际 PE 产品名、版本和回包签名；打包不能再改写它。 |
| `mihomo-core.exe` | 固定 core SHA 编译出的二开 Go 内核 | 在 Foundation 接受二开来源后签署。 | 核实并补齐 PE 产品资源；当前 Go 版本字符串不是 Windows PE 产品元数据。 |
| `mihomo-desktop-service.exe` | 本仓库 Rust 服务 | 用本项目获批政策签署。 | 服务 feature 当前不执行桌面 Tauri 资源生成；需要实际检查并补齐 PE 元数据。 |
| NSIS 生成的卸载程序 | 锁定的 Tauri/NSIS 生成结果 | 单独设计获批签名阶段或签名 hook。 | **未实现。** 外层 setup 签名不会使它自动签名；必须验证安装后实际卸载程序。 |
| `*-setup.exe` | 打包以上运行程序的 NSIS 安装器 | 内部文件完成后再签外层 PE。 | 外层签名、回包验证和最终安装验收尚未接入。 |
| `mihomo-update-verify.exe` | 固定下载并校验的第三方 GitHub CLI | 保留原始第三方字节和许可，不用本项目证书无差别重签。 | 在申请中披露第三方组成，验证已有签名或按获批配置保留。 |
| 其他第三方 EXE/DLL 或 NSIS 插件 | 第三方组件 | 显式列明；按获批规则保留或验证供应方签名。 | 不使用覆盖所有 `*.exe` / `*.dll` 的无差别签署规则。 |

Foundation 要求签署对象的产品元数据被设置并约束，不能只以文件名匹配代替。当前 `prepare-desktop.py` 的 Go 构建没有显式添加产品资源，`src-tauri/build.rs` 只在 desktop feature 下执行 Tauri 构建助手。后续实现应检查真实产物，再选择可复现的资源生成/补写方法；可在桌面构建阶段处理，不必改动 core 的功能源码。[3][7]

不要把桌面发行版本与 core 运行时版本混为一谈。core 的内部版本和源码 SHA 继续记录在 build-info；待签 PE 的产品版本策略应先和 Foundation 确认，并使实际二进制满足配置。

## 4. 正式接入的执行顺序

应在现有 `windows-desktop.yml` 的受控手动发行路径中实现以下阶段。该设计可复用已有编译和 Windows 验收，但当前仓库**没有实现这些 SignPath 阶段**。

### A. 编译、元数据与请求准备

1. 检出本次允许的 `clash-dashboard` 提交；继续按 `desktop/core.json` 检出 core 的完整 SHA。运行现有源码、版本和 coreDirty 检查。
2. 安装锁定依赖，构建 UI、core、服务及主程序。主程序可以用 `pnpm exec tauri build --ci --target x86_64-pc-windows-msvc --no-bundle`，将编译与最终打包分开。
3. 完成并检查 PE 产品资源。将许可允许且获批的运行程序复制到单独的待签目录，准确记录输入文件和哈希；不要把更新 verifier 或 NSIS 第三方 DLL 放进任意通配签署范围。
4. 用 `actions/upload-artifact` v4 或以上上传本次产物，取得步骤输出的 `artifact-id`。默认上传结果为 ZIP，所以 artifact configuration 需要匹配外层 ZIP 结构及内部准确路径。[4]

### B. 运行程序签名

5. 调用锁定的 SignPath Action，提供真实 org/project/policy/artifact configuration，设置 `wait-for-completion: true` 和输出目录。
6. 由 SignPath 中实际配置的 approver 审核来源、版本、构建证据和文件范围，再批准或拒绝。默认等待时间为 600 秒；正式实施应为人工操作显式设置合理的 action 和 job 超时，并显示签名请求链接供审批。[4][5]
7. 下载到全新的 `runtime-signed` 目录；先检查预期文件集合、路径、PE 元数据和 Authenticode，再将通过验证的文件覆盖到准确的打包输入路径。解压后的未知文件、缺失签名或错误发布者都必须失败，不得混合 unsigned fallback。

### C. NSIS 卸载程序和最终两包

8. **先完成 NSIS 卸载程序签名设计。** 锁定的 Tauri CLI `2.12.3` 在 Windows bundler 中提供卸载程序签名命令路径，但本项目尚未接入 SignPath。必须确认可信 artifact 上传、人工审批、回包验证和打包 hook 如何衔接；如产生更多请求，每个请求照政策审批。不要仅在 Tauri 配置中填一个不存在的签名命令。
9. 对已签运行程序执行单独打包，例如 `pnpm exec tauri bundle --ci --target x86_64-pc-windows-msvc --bundles nsis --no-binary-patching`。该参数在锁定版本中存在，用于防止打包时重写主程序而破坏签名。本项目只有 NSIS 安装格式并使用自己的更新器；仍需验证实际打包结果和 sidecar 字节。[8][9]
10. 将最终 setup EXE 单独上传并按安装器配置提交签名请求，等待批准和回包。SignPath 官方将 PE 列为非复合格式，未列 NSIS 深度签名格式；不能假定签署外层安装器会重签其内部 EXE。[7]
11. 用通过验证的运行程序生成 portable ZIP；使用通过验证的 setup EXE 作为安装版。两包中的运行程序应对应同一组已签产物。保留第三方工具的原始字节。
12. 在 Windows 上检查签名并运行现有实际安装/卸载、WebView2、代理、TUN 与清理验收；验证安装后的主程序、服务、core，以及设计要求签署的卸载程序。不能只验证提交前的 unsigned candidate。

### D. 最终证明与发布

13. Authenticode 会改变文件字节。对最终 setup EXE 和 portable ZIP 重新生成现有 GitHub Actions attestation，再执行现有生产策略验证和篡改拒绝检查。
14. 在所有最终附件就位后重新执行 `scripts/package-desktop.py --checksums-only`。不要保留签名前的 `.sigstore.json` 或 checksum 清单。
15. 由现有发布脚本发布新的桌面版本。正式说明仅在真实签名验证通过后陈述覆盖范围；未签的第三方或卸载组件若获准保留，必须如实说明。

## 5. 保持现有更新器的工作流身份

当前 `src-tauri/src/update.rs` 和 release workflow 共同限制以下来源：

- 仓库：`chummumm/mihomo-optimized`，repository ID `1410715865`。
- signer workflow：`chummumm/mihomo-optimized/.github/workflows/windows-desktop.yml`。
- source ref：`refs/heads/clash-dashboard`。
- source digest 与 signer digest：该次发行的同一个完整提交 SHA。
- GitHub OIDC issuer、GitHub 托管 runner，以及项目附带的信任根策略。

因此，同目录的 [windows-desktop-signpath.yml.example](windows-desktop-signpath.yml.example) 只是动作输入与阶段顺序的参考。**不能把它改名成一个新的正式发布 workflow，就假定现有客户端会接受该 workflow 产生的证明。** 最小正式改法是把获审的签名步骤接入原来的 `windows-desktop.yml`，由原工作流在签名完成后继续证明和发布。

若确实要迁移 signer workflow 身份，需要另行设计旧客户端的升级兼容与验证策略，并重新审查；本准备 PR 不做这项迁移。

## 6. 样例的停止条件与验证要求

`.yml.example` 使用明确的 `workflow_dispatch`，限定分支，预检真实配置、输入提交及版本。文件位于 `docs/` 且没有 `.yml` 结尾，不会被 Actions 执行。即使有人复制它到 `.github/workflows/`，其中的无条件停止步骤也会阻止签名；配置齐全不表示尚缺的构建和 NSIS 集成自动完成。

样例展示两个实际 Action 调用的参数契约，但没有实现 PE 资源准备、完整构建 staging、回包验证、卸载程序签署和正式发布。删除停止步骤前，必须先在另一个 PR 中完成上文缺口和实际验证。无批准、缺配置、等待超时、签名被拒绝、输出不完整或证书不符时，正式路径一律失败，不能 `continue-on-error` 后发布所谓签名包。

Windows 验证可使用 `Get-AuthenticodeSignature`，但不能只判断证书对象存在。至少检查：

- `Status` 为 `Valid`；具有预期的发布者证书和经过验证的时间戳。
- 从 `SignerCertificate.RawData` 显式计算 SHA-256，与独立配置的获批证书指纹相等；不要直接把 `Thumbprint` 字段当成所要求的 SHA-256。[10]
- 准确文件集合和 PE 产品元数据符合获批 artifact configuration；第三方文件与输入一致。
- portable ZIP 中实际运行的文件、安装后实际文件以及最终 setup 都按预定范围检查。
- 最终下载文件的 SHA256SUMS 和现有 Sigstore 验证均通过。

SignPath 回包不能替代以上本地检查；GitHub environment 审批、workflow_dispatch 或 token 存在也不能替代 Foundation 的审批结果。

## 7. 版本、重试与可公开状态

- 现有 unsigned 发行继续使用真实的“未配置 Authenticode 发布者证书”说明，不能用本准备 PR 提前改成“已签名”。
- 获批后使用新的桌面版本发布，同步实际版本文件；不能覆盖已发布版本的 assets 或悄悄重算原清单。core pin 无需因签名接入而变动。
- 一次签名等待超时不等于请求被取消。保留 request ID/链接，确认原请求状态后再按获批流程恢复；不要盲目重跑制造多个待批准请求。
- CI token 不含人的审批权限。发布稿中的签名状态只能来自最终产物验证结果，并明确实际覆盖文件。
- 申请尚未提交、审核中、获批待接入、test-signing 通过、正式签名通过是不同状态，应按实际进度更新文档。

## 官方参考与核对版本

以下文档于 2026-10-19 核对；实际开通时应复核其变化。URL 指向官方来源，GitHub Actions 的执行版本使用完整提交锁定。

1. [SignPath：Authenticode 与支持格式](https://docs.signpath.io/artifact-configuration/reference)
2. [Tauri：Windows Code Signing 与 SmartScreen 说明](https://v2.tauri.app/distribute/sign/windows/)
3. [SignPath Foundation：开源项目条款](https://signpath.org/terms)
4. [SignPath：GitHub Trusted Build System 与 Action 参数](https://docs.signpath.io/trusted-build-systems/github)
5. [SignPath：项目、签名政策与审批](https://docs.signpath.io/projects)
6. [SignPath：CI 用户和 API token](https://docs.signpath.io/users/)
7. [SignPath：artifact configuration 参考与 PE 元数据限制](https://docs.signpath.io/artifact-configuration/reference)
8. [Tauri CLI：build / bundle 参数](https://v2.tauri.app/reference/cli/)
9. [锁定 Tauri CLI 2.12.3：bundle 源码](https://github.com/tauri-apps/tauri/blob/tauri-cli-v2.12.3/crates/tauri-cli/src/bundle.rs)、[NSIS bundler 源码](https://github.com/tauri-apps/tauri/blob/tauri-cli-v2.12.3/crates/tauri-bundler/src/bundle/windows/nsis/mod.rs)
10. [Microsoft：Get-AuthenticodeSignature](https://learn.microsoft.com/powershell/module/microsoft.powershell.security/get-authenticodesignature)

Action 锁定：

- [SignPath submit-signing-request v3 / `f6d04783b4569d051e0c80105fe66e82819d0092`](https://github.com/SignPath/github-action-submit-signing-request/blob/f6d04783b4569d051e0c80105fe66e82819d0092/action.yml)
- [actions/upload-artifact v4 / `ea165f8d65b6e75b540449e92b4886f43607fa02`](https://github.com/actions/upload-artifact/tree/ea165f8d65b6e75b540449e92b4886f43607fa02)

使用 Action 自带的官方 connector URL 默认值；没有自托管需求时不自行填写或覆盖端点。正式实现前复核锁定版本，不用未审查的移动标签替代。
