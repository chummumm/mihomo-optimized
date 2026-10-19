$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This integration setup is restricted to disposable GitHub Windows runners.' }
$out = Join-Path $PWD 'artifacts/verification'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$installer = @(Get-ChildItem 'artifacts/desktop/*-setup.exe')
if ($installer.Count -ne 1) { throw 'Expected one installer.' }
$process = Start-Process -FilePath $installer[0].FullName -ArgumentList '/S' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Installer failed with $($process.ExitCode)" }
$application = Join-Path $env:ProgramFiles 'Mihomo Optimized/mihomo-optimized.exe'
if (-not (Test-Path $application)) { throw "Installed application missing: $application" }
$runtimes = @(
  "${env:ProgramFiles(x86)}/Microsoft/EdgeWebView/Application/*/msedgewebview2.exe",
  "$env:ProgramFiles/Microsoft/EdgeWebView/Application/*/msedgewebview2.exe"
)
$runtime = $runtimes | ForEach-Object { Get-ChildItem $_ -ErrorAction SilentlyContinue } | Sort-Object { [version]$_.VersionInfo.FileVersion } -Descending | Select-Object -First 1
if (-not $runtime) { throw 'WebView2 runtime was not installed.' }
$version = $runtime.VersionInfo.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid WebView2 version.' }
$archive = Join-Path $env:RUNNER_TEMP 'mihomo-edgedriver.zip'
Invoke-WebRequest "https://msedgedriver.microsoft.com/$version/edgedriver_win64.zip" -OutFile $archive
$driverDirectory = Join-Path $env:RUNNER_TEMP 'mihomo-edgedriver'
Expand-Archive $archive -DestinationPath $driverDirectory -Force
$driver = Join-Path $driverDirectory 'msedgedriver.exe'
if (-not (Test-Path $driver)) { throw 'Edge WebDriver download is incomplete.' }
"MIHOMO_TEST_APP=$application" >> $env:GITHUB_ENV
"MIHOMO_TEST_DRIVER=$driver" >> $env:GITHUB_ENV
@{ webview2Version = $version; installerExitCode = $process.ExitCode; application = $application } | ConvertTo-Json | Set-Content (Join-Path $out 'installation.json')
