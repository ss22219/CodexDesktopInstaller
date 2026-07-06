param(
    [switch]$SkipRebuild
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) -and [string]::IsNullOrWhiteSpace($env:OS)) {
    $env:OS = "Windows_NT"
}

$root = Split-Path $PSScriptRoot -Parent
$rebuildDir = "$root/../CodexDesktop-Rebuild"

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $previousNativePreference = $PSNativeCommandUseErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $PSNativeCommandUseErrorActionPreference = $false
        & $Command
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $previousNativePreference
    }

    if ($exitCode -ne 0) {
        throw "$Label 失败，退出码: $exitCode"
    }
}

function Add-SevenZipShimToPath {
    $candidates = @(
        "$env:ProgramFiles\7-Zip\7zz.exe",
        "$env:ProgramFiles\7-Zip\7z.exe",
        "$root\Bundle\Tools\7zip\7z.exe"
    )

    $sevenZipExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sevenZipExe) {
        return
    }

    $shimDir = Join-Path $root ".build-tools"
    New-Item -ItemType Directory -Force -Path $shimDir | Out-Null
    $shimPath = Join-Path $shimDir "7zz.cmd"
    "@echo off`r`n`"$sevenZipExe`" %*" | Set-Content -LiteralPath $shimPath -Encoding ASCII
    $env:PATH = "$shimDir;$env:PATH"
}

Write-Host "===== Codex Desktop Installer 构建脚本 =====" -ForegroundColor Cyan
Write-Host ""

# Step 1: 构建 Codex Desktop (如果未跳过)
if (-not $SkipRebuild) {
    Write-Host "[Step 1/4] 构建 Codex Desktop (CodexDesktop-Rebuild)..." -ForegroundColor Cyan

    if (-not (Test-Path $rebuildDir)) {
        Write-Host "  克隆 CodexDesktop-Rebuild..." -ForegroundColor Yellow
        git clone "https://github.com/Haleclipse/CodexDesktop-Rebuild.git" $rebuildDir 2>&1
    }

    Push-Location $rebuildDir
    try {
        Add-SevenZipShimToPath

        Write-Host "  npm install..." -ForegroundColor DarkGray
        Invoke-NativeChecked -Label "npm install" -Command { npm install 2>&1 | Out-Null }

        Write-Host "  构建 Windows x64..." -ForegroundColor DarkGray
        Invoke-NativeChecked -Label "Codex Desktop 构建" -Command { npm run build:win-x64 2>&1 }

        Write-Host "  OK: Codex Desktop 构建完成" -ForegroundColor Green
    } finally {
        Pop-Location
    }
} else {
    Write-Host "[Step 1/4] 跳过 Codex Desktop 构建" -ForegroundColor Yellow
    Write-Host ""
}

# Step 2: 准备 Bundle
Write-Host "[Step 2/4] 准备 Bundle 资源..." -ForegroundColor Cyan
Invoke-NativeChecked -Label "准备 Bundle" -Command { & "$root/scripts/prepare-bundle.ps1" 2>&1 }

# Step 3: 配置 Bundle 复制到输出目录
Write-Host "[Step 3/4] 配置项目..." -ForegroundColor Cyan
$bundleTarget = "$root/CodexInstaller.Desktop/Bundle"
if (Test-Path $bundleTarget) { Remove-Item $bundleTarget -Recurse -Force }
Copy-Item "$root/Bundle" $bundleTarget -Recurse -Force

# 更新 csproj 确保 Bundle 复制到输出
$csproj = "$root/CodexInstaller.Desktop/CodexInstaller.Desktop.csproj"
$content = Get-Content $csproj -Raw
if ($content -notmatch 'Bundle\*\*') {
    $insert = @'
  <ItemGroup>
    <Content Include="Bundle\**">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
'@
    $content = $content -replace '(</Project>)', "$insert`n</Project>"
    Set-Content $csproj $content -Encoding UTF8
}

# Step 4: 构建 Avalonia 安装程序
Write-Host "[Step 4/4] AOT 构建 Avalonia 安装程序..." -ForegroundColor Cyan
Push-Location $root
try {
    $outDir = "$root/CodexInstaller.Desktop/bin/Release/net10.0/win-x64/publish"
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    dotnet publish "$root/CodexInstaller.Desktop/CodexInstaller.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:DebugType=None -p:DebugSymbols=false -o $outDir 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "===== 构建成功! =====" -ForegroundColor Green
        $installerExe = "$outDir/CodexInstaller.Desktop.exe"
        $namedInstallerExe = "$outDir/Codex 安装.exe"
        if (Test-Path $installerExe) {
            Remove-Item $namedInstallerExe -Force -ErrorAction SilentlyContinue
            Move-Item $installerExe $namedInstallerExe -Force
        }
        Get-ChildItem -LiteralPath $outDir -Filter "*.pdb" -File | Remove-Item -Force
        Write-Host "安装程序位置: $outDir" -ForegroundColor Cyan
        Write-Host "运行: $outDir/Codex 安装.exe" -ForegroundColor Cyan
    } else {
        Write-Host "构建失败!" -ForegroundColor Red
        exit $LASTEXITCODE
    }
} finally {
    Pop-Location
}
