$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This integration setup is restricted to disposable GitHub Windows runners.' }
$out = Join-Path $PWD 'artifacts/verification'
New-Item -ItemType Directory -Path $out -Force | Out-Null
python (Join-Path $PSScriptRoot 'upgrade_smoke.py')
if ($LASTEXITCODE -ne 0) { throw 'Uninstall-first and running-service upgrade acceptance failed; see upgrade.json.' }
$upgrade = Get-Content (Join-Path $out 'upgrade.json') -Raw | ConvertFrom-Json
if ($upgrade.passed -ne $true) { throw 'Upgrade acceptance did not report success.' }
$application = Join-Path $env:ProgramFiles 'Mihomo Optimized/mihomo-optimized.exe'
if (-not (Test-Path $application)) { throw "Installed application missing: $application" }
$hostProgram = Join-Path $env:ProgramFiles 'Mihomo Optimized/mihomo-desktop-host.exe'
if (-not (Test-Path $hostProgram)) { throw "Installed native host missing: $hostProgram" }
$runtimeFiles = Get-ChildItem (Split-Path $application) -Recurse -File
$unexpected = $runtimeFiles | Where-Object { $_.Name -match '(?i)webview2|tauri|wry|chrome_elf|msedge|libcef|resources\.pak' }
if ($unexpected) { throw 'A browser runtime was installed with the native application.' }
"MIHOMO_TEST_APP=$application" >> $env:GITHUB_ENV
@{ framework = 'WPF'; selfContained = $true; browserRuntime = $false; installerExitCode = 0; application = $application; upgradeEvidence = 'upgrade.json' } | ConvertTo-Json | Set-Content (Join-Path $out 'installation.json')
