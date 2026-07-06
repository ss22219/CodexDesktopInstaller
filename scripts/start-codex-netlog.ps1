param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\CodexFreeLauncher",
    [string]$LogPath = "$env:USERPROFILE\Desktop\codex-netlog.json",
    [switch]$StopExisting
)

$ErrorActionPreference = "Stop"

$codexExe = Join-Path $InstallDir "Codex.exe"
if (-not (Test-Path -LiteralPath $codexExe)) {
    throw "Codex.exe not found: $codexExe"
}

if ($StopExisting) {
    Get-Process -Name "Codex" -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.MainModule.FileName -ne $codexExe) { return }
            $_.Kill($true)
            $_.WaitForExit(3000)
        } catch {
        } finally {
            $_.Dispose()
        }
    }
}

$logParent = Split-Path -Parent $LogPath
if (-not (Test-Path -LiteralPath $logParent)) {
    New-Item -ItemType Directory -Path $logParent | Out-Null
}

Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue

$args = @(
    "--log-net-log=$LogPath",
    "--net-log-capture-mode=Everything",
    "--remote-debugging-port=9227"
)

$env:CODEX_HOME = Join-Path $InstallDir "Data\.codex"
$process = Start-Process -FilePath $codexExe -ArgumentList $args -PassThru
"started_pid=$($process.Id)"
"netlog=$LogPath"
"reproduce the issue, then fully quit Codex before analyzing the log"
