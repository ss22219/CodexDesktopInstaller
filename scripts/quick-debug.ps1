param(
    [switch]$RunInstaller,
    [switch]$RunLauncher,
    [switch]$SyncBundle
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$bundleSource = Join-Path $root "Bundle"
$bundleTarget = Join-Path $root "CodexInstaller.Desktop\Bundle"

if ($SyncBundle -or !(Test-Path (Join-Path $bundleTarget "Archives\CodexDesktop.7z"))) {
    if (!(Test-Path $bundleSource)) {
        throw "Bundle source not found: $bundleSource"
    }

    New-Item -ItemType Directory -Force -Path $bundleTarget | Out-Null
    Copy-Item -LiteralPath (Join-Path $bundleSource "*") -Destination $bundleTarget -Recurse -Force
}

dotnet build (Join-Path $root "CodexDesktopInstaller.slnx") -c Debug

if ($RunInstaller) {
    $installerExe = Join-Path $root "CodexInstaller.Desktop\bin\Debug\net10.0\CodexInstaller.Desktop.exe"
    Start-Process -FilePath $installerExe
}

if ($RunLauncher) {
    $launcherExe = Join-Path $root "CodexLauncher\bin\Debug\net10.0\CodexLauncher.exe"
    Start-Process -FilePath $launcherExe
}
