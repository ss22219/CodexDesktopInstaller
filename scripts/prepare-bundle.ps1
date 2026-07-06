param(
    [string]$BundleDir = "$PSScriptRoot/../Bundle",
    [string]$RebuildOutputDir = "$PSScriptRoot/../../CodexDesktop-Rebuild/out/win/Codex-win32-x64",
    [string]$LauncherProject = "$PSScriptRoot/../CodexLauncher/CodexLauncher.csproj",
    [string]$ProxyProject = "$PSScriptRoot/../CodexApiProxy/CodexApiProxy.csproj",
    [string]$CodexPatchScript = "$PSScriptRoot/patch-codex-app.ps1",
    [string]$CangheSkillsSource = "$PSScriptRoot/../../canghe-skills/skills",
    [string]$HyperFramesPluginSource = "$env:USERPROFILE/.codex/plugins/cache/openai-curated-remote/hyperframes"
)

if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows) -and [string]::IsNullOrWhiteSpace($env:OS)) {
    $env:OS = "Windows_NT"
}

function Get-SevenZipHome {
    $candidates = @(
        "$env:ProgramFiles\7-Zip",
        "${env:ProgramFiles(x86)}\7-Zip"
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ((Test-Path "$candidate\7z.exe") -and (Test-Path "$candidate\7z.dll")) {
            return $candidate
        }
    }

    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $sevenZipResolvedHome = Split-Path $cmd.Source -Parent
        if ((Test-Path "$sevenZipResolvedHome\7z.exe") -and (Test-Path "$sevenZipResolvedHome\7z.dll")) {
            return $sevenZipResolvedHome
        }
    }

    throw "未找到可打包的 7-Zip。请先安装 7-Zip，并确保 7z.exe 与 7z.dll 在同一目录。"
}

function Get-NodeHome {
    $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $nodeResolvedHome = Split-Path $cmd.Source -Parent
        if ((Test-Path "$nodeResolvedHome\node.exe") -and (Test-Path "$nodeResolvedHome\npm.cmd") -and (Test-Path "$nodeResolvedHome\npx.cmd")) {
            return $nodeResolvedHome
        }
    }

    $candidates = @(
        "$env:ProgramFiles\nodejs",
        "${env:ProgramFiles(x86)}\nodejs",
        "C:\nvm4w\nodejs"
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ((Test-Path "$candidate\node.exe") -and (Test-Path "$candidate\npm.cmd") -and (Test-Path "$candidate\npx.cmd")) {
            return $candidate
        }
    }

    return $null
}

function Convert-EncodedPackageName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return $Name.Replace('%40', '@').Replace('%2B', '+').Replace('%24', '$')
}

function Normalize-ScopedNodeModules {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        return
    }

    $encodedScopeDirs = Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { (Convert-EncodedPackageName -Name $_.Name) -ne $_.Name } |
        Sort-Object { $_.FullName.Length } -Descending

    foreach ($source in $encodedScopeDirs) {
        if (-not (Test-Path -LiteralPath $source.FullName)) {
            continue
        }

        $targetName = Convert-EncodedPackageName -Name $source.Name
        $target = Join-Path $source.Parent.FullName $targetName
        if (-not (Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $source.FullName -Destination $target -Force
            continue
        }

        Get-ChildItem -LiteralPath $source.FullName -Force -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force
        }
        Remove-Item -LiteralPath $source.FullName -Recurse -Force
    }

    $encodedFiles = Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { (Convert-EncodedPackageName -Name $_.Name) -ne $_.Name } |
        Sort-Object { $_.FullName.Length } -Descending

    foreach ($source in $encodedFiles) {
        if (-not (Test-Path -LiteralPath $source.FullName)) {
            continue
        }

        $targetName = Convert-EncodedPackageName -Name $source.Name
        $target = Join-Path $source.DirectoryName $targetName
        if (-not (Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $source.FullName -Destination $target -Force
            continue
        }

        Copy-Item -LiteralPath $source.FullName -Destination $target -Force
        Remove-Item -LiteralPath $source.FullName -Force
    }
}

function Get-VcRuntimeDlls {
    $names = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')
    $roots = @(
        "$env:WINDIR\System32",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Redist\MSVC"
    )

    foreach ($name in $names) {
        $found = $null
        foreach ($root in $roots) {
            if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root)) {
                continue
            }

            $direct = Join-Path $root $name
            if (Test-Path -LiteralPath $direct) {
                $found = Get-Item -LiteralPath $direct
                break
            }

            $found = Get-ChildItem -LiteralPath $root -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\|\\System32\\|\\IDE\\' -and $_.FullName -notmatch 'debug_nonredist|spectre' } |
                Select-Object -First 1
            if ($found) {
                break
            }
        }

        if ($found) {
            $found
        } else {
            Write-Host "  WARN: 未找到 $name，干净系统可能需要 VC++ 运行库。" -ForegroundColor Yellow
        }
    }
}

function Copy-VcRuntimeDllsToCodexRuntime {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppDir
    )

    $dlls = @(Get-VcRuntimeDlls)
    if ($dlls.Count -eq 0) {
        return
    }

    $targets = @(
        "$AppDir/resources/cua_node/bin",
        "$AppDir/resources/cua_node/bin/node_modules/@oai/sky/bin/windows"
    )

    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target)) {
            continue
        }

        foreach ($dll in $dlls) {
            Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
        }
    }

    Write-Host "  OK: 已内置 VC++ 运行库 DLL" -ForegroundColor Green
}

Write-Host "[1/8] 创建 Bundle 目录..." -ForegroundColor Cyan
@('CodexDesktop', 'Archives', 'Tools/7zip', 'Tools/Node', 'Launcher', 'Config/codex', 'Skills', 'Plugins/openai-curated-remote') | ForEach-Object {
    $null = New-Item -ItemType Directory -Path "$BundleDir/$_" -Force
}

@('CodexDesktop', 'Archives', 'Tools/7zip', 'Launcher', 'Config/codex', 'Config/agents', 'Config/opencode', 'Skills', 'Plugins') | ForEach-Object {
    $path = "$BundleDir/$_"
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

@('CodexDesktop', 'Archives', 'Tools/7zip', 'Tools/Node', 'Launcher', 'Config/codex', 'Skills', 'Plugins/openai-curated-remote') | ForEach-Object {
    $null = New-Item -ItemType Directory -Path "$BundleDir/$_" -Force
}

Write-Host "[2/8] 复制 Codex Desktop 二进制..." -ForegroundColor Cyan
if (Test-Path $RebuildOutputDir) {
    $codexRootExe = Join-Path $RebuildOutputDir "Codex.exe"
    $codexResourceExe = Join-Path $RebuildOutputDir "resources\codex.exe"
    if ((-not (Test-Path -LiteralPath $codexRootExe)) -and (-not (Test-Path -LiteralPath $codexResourceExe))) {
        throw "Codex Desktop 构建输出不完整，缺少 Codex.exe: $RebuildOutputDir"
    }

    Copy-Item -Path "$RebuildOutputDir/*" -Destination "$BundleDir/CodexDesktop/" -Recurse -Force
    Write-Host "  OK: 从 $RebuildOutputDir 复制" -ForegroundColor Green

    if (Test-Path $CodexPatchScript) {
        Write-Host "  正在应用 Codex API 模式补丁..." -ForegroundColor DarkGray
        & $CodexPatchScript -AppDir "$BundleDir/CodexDesktop"
        if ($LASTEXITCODE -ne 0) {
            throw "Codex App 补丁失败，退出码 $LASTEXITCODE"
        }
    } else {
        Write-Host "  WARN: 未找到 Codex 补丁脚本: $CodexPatchScript" -ForegroundColor Yellow
    }

    Normalize-ScopedNodeModules -Root "$BundleDir/CodexDesktop/resources/cua_node/bin/node_modules"
    Copy-VcRuntimeDllsToCodexRuntime -AppDir "$BundleDir/CodexDesktop"
} else {
    Write-Host "  WARN: 未找到 Codex Desktop 构建输出: $RebuildOutputDir" -ForegroundColor Yellow
    Write-Host "  请先运行 CodexDesktop-Rebuild 的 npm run build:win-x64" -ForegroundColor Yellow
}

Write-Host "[3/8] 打包 7-Zip 解压工具..." -ForegroundColor Cyan
$sevenZipHome = Get-SevenZipHome
$sevenZipTarget = "$BundleDir/Tools/7zip"
@('7z.exe', '7z.dll', '7-zip.dll', 'License.txt', 'readme.txt') | ForEach-Object {
    $src = "$sevenZipHome/$_"
    if (Test-Path $src) {
        Copy-Item $src $sevenZipTarget -Force
    }
}
Write-Host "  OK: $sevenZipHome" -ForegroundColor Green

Write-Host "  正在打包 Node.js 运行时..." -ForegroundColor DarkGray
$nodeHome = Get-NodeHome
if ($nodeHome) {
    $nodeTarget = "$BundleDir/Tools/Node"
    @('node.exe', 'nodevars.bat', 'npm', 'npm.cmd', 'npm.ps1', 'npx', 'npx.cmd', 'npx.ps1', 'corepack', 'corepack.cmd', 'corepack.ps1', 'LICENSE', 'README.md') | ForEach-Object {
        $src = "$nodeHome/$_"
        if (Test-Path $src) {
            Copy-Item $src $nodeTarget -Force
        }
    }

    $nodeModulesTarget = "$nodeTarget/node_modules"
    $null = New-Item -ItemType Directory -Path $nodeModulesTarget -Force
    @('npm', 'corepack') | ForEach-Object {
        $src = "$nodeHome/node_modules/$_"
        if (Test-Path $src) {
            Copy-Item -LiteralPath $src -Destination $nodeModulesTarget -Recurse -Force
        }
    }
    Write-Host "  OK: Node.js $(& "$nodeHome/node.exe" -v)" -ForegroundColor Green
} else {
    Write-Host "  WARN: 未找到可打包的 Node.js，将跳过内置 Node。" -ForegroundColor Yellow
}

Write-Host "[4/8] 压缩 Codex Desktop 资源包..." -ForegroundColor Cyan
$codexFiles = @(Get-ChildItem "$BundleDir/CodexDesktop" -Recurse -File -ErrorAction SilentlyContinue)
if ($codexFiles.Count -gt 0) {
    $archivePath = "$BundleDir/Archives/CodexDesktop.7z"
    if (Test-Path $archivePath) { Remove-Item $archivePath -Force }

    Push-Location "$BundleDir/CodexDesktop"
    try {
        & "$sevenZipHome/7z.exe" a -t7z "$archivePath" ".\*" -m0=LZMA2 -mx=1 -mmt=on -y
        if ($LASTEXITCODE -ne 0) {
            throw "7-Zip 压缩失败，退出码 $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }

    Remove-Item "$BundleDir/CodexDesktop" -Recurse -Force
    $null = New-Item -ItemType Directory -Path "$BundleDir/CodexDesktop" -Force
    $archiveSize = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)
    Write-Host "  OK: CodexDesktop.7z ($archiveSize MB)" -ForegroundColor Green
} else {
    Write-Host "  WARN: Codex Desktop 目录为空，跳过压缩。" -ForegroundColor Yellow
}

Write-Host "[5/8] 编译并打包 Codex 启动..." -ForegroundColor Cyan
if (-not (Test-Path $LauncherProject)) {
    throw "未找到 Codex 启动项目: $LauncherProject"
}
if (-not (Test-Path $ProxyProject)) {
    throw "未找到 API 转换器项目: $ProxyProject"
}
dotnet publish $LauncherProject -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:DebugType=None -p:DebugSymbols=false -o "$BundleDir/Launcher"
if ($LASTEXITCODE -ne 0) {
    throw "Codex 启动编译失败，退出码 $LASTEXITCODE"
}
$launcherExe = "$BundleDir/Launcher/CodexLauncher.exe"
$namedLauncherExe = "$BundleDir/Launcher/Codex 启动.exe"
if (Test-Path $launcherExe) {
    if (Test-Path $namedLauncherExe) { Remove-Item $namedLauncherExe -Force }
    Move-Item $launcherExe $namedLauncherExe -Force
}
dotnet publish $ProxyProject -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:DebugType=None -p:DebugSymbols=false -o "$BundleDir/Launcher"
if ($LASTEXITCODE -ne 0) {
    throw "API 转换器编译失败，退出码 $LASTEXITCODE"
}
Get-ChildItem "$BundleDir/Launcher" -Filter '*.pdb' -Recurse -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Write-Host "  OK: $BundleDir/Launcher" -ForegroundColor Green

Write-Host "[6/8] 准备空 Codex 配置目录..." -ForegroundColor Cyan
Write-Host "  OK: 不复制本机个人 .codex 配置" -ForegroundColor Green

Write-Host "[7/8] 复制 canghe Skills..." -ForegroundColor Cyan
$targetSkills = "$BundleDir/Skills"

if (Test-Path $CangheSkillsSource) {
    Get-ChildItem $CangheSkillsSource -Directory | ForEach-Object {
        $target = "$targetSkills/$($_.Name)"
        if (Test-Path $target) {
            Remove-Item $target -Recurse -Force
        }
        Copy-Item $_.FullName $target -Recurse -Force
        Write-Host "  Canghe Skill: $($_.Name)" -ForegroundColor DarkGray
    }
} else {
    Write-Host "  WARN: 未找到 canghe-skills: $CangheSkillsSource" -ForegroundColor Yellow
}

Get-ChildItem $targetSkills -Directory -Filter '.git' -Recurse -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force

Write-Host "[8/8] 复制 HyperFrames by HeyGen 插件..." -ForegroundColor Cyan
$targetPluginRoot = "$BundleDir/Plugins/openai-curated-remote"
$targetHyperFrames = "$targetPluginRoot/hyperframes"

if (Test-Path $HyperFramesPluginSource) {
    if (Test-Path $targetHyperFrames) {
        Remove-Item $targetHyperFrames -Recurse -Force
    }
    Copy-Item -LiteralPath $HyperFramesPluginSource -Destination $targetPluginRoot -Recurse -Force
    Write-Host "  OK: HyperFrames by HeyGen" -ForegroundColor Green
} else {
    Write-Host "  WARN: 未找到 HyperFrames 插件: $HyperFramesPluginSource" -ForegroundColor Yellow
}

Get-ChildItem "$BundleDir/Plugins" -Directory -Filter '.git' -Recurse -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force

Write-Host "`n完成! Bundle 已准备好:" -ForegroundColor Green
Get-ChildItem -LiteralPath $BundleDir -Force | ForEach-Object {
    if ($_.PSIsContainer) {
        $files = @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue)
        $size = ($files | Measure-Object Length -Sum).Sum
        Write-Host "  $($_.Name): $($files.Count) 文件, $([math]::Round($size/1KB, 1)) KB"
    } else {
        Write-Host "  $($_.Name): 1 文件, $([math]::Round($_.Length/1KB, 1)) KB"
    }
}
