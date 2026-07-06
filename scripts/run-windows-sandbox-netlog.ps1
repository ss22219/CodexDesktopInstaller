param(
    [string]$PackageZip = "$PSScriptRoot\..\dist\CodexInstaller.zip",
    [string]$OutputDir = "$env:USERPROFILE\Desktop\CodexSandboxNetlog",
    [int]$CaptureSeconds = 45,
    [int]$TimeoutSeconds = 420
)

$ErrorActionPreference = "Stop"

$sandboxExe = (Get-Command WindowsSandbox.exe -ErrorAction Stop).Source
$packageZipPath = (Resolve-Path -LiteralPath $PackageZip).Path

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$outputDirPath = (Resolve-Path -LiteralPath $OutputDir).Path
$packageCopy = Join-Path $outputDirPath "CodexInstaller.zip"
$packageDir = Join-Path $outputDirPath "Package"
$guestScript = Join-Path $outputDirPath "run-in-sandbox.ps1"
$wsbPath = Join-Path $outputDirPath "CodexNetlog.wsb"
$donePath = Join-Path $outputDirPath "sandbox-done.json"
$errorPath = Join-Path $outputDirPath "sandbox-error.txt"
$netlogPath = Join-Path $outputDirPath "codex-netlog-sandbox.json"
$transcriptHostPath = Join-Path $outputDirPath "sandbox-transcript.txt"

Remove-Item -LiteralPath $donePath, $errorPath, $netlogPath, $transcriptHostPath -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $packageZipPath -Destination $packageCopy -Force
if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir | Out-Null

$hostSevenZipCandidates = @(
    "$env:ProgramFiles\7-Zip\7z.exe",
    "$env:ProgramFiles\7-Zip\7zz.exe"
)
$hostSevenZip = $hostSevenZipCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $hostSevenZip) {
    throw "7-Zip not found on host. Install 7-Zip or add it to: $($hostSevenZipCandidates -join ', ')"
}

& $hostSevenZip x $packageCopy "-o$packageDir" -y -bb0 -bso0 -bsp0
if ($LASTEXITCODE -ne 0) {
    throw "Failed to extract package zip on host: $LASTEXITCODE"
}

$guestScriptContent = @"
`$ErrorActionPreference = 'Stop'
`$share = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$donePath = Join-Path `$share 'sandbox-done.json'
`$errorPath = Join-Path `$share 'sandbox-error.txt'
`$netlogPath = Join-Path `$share 'codex-netlog-sandbox.json'
`$transcriptPath = Join-Path `$share 'sandbox-transcript.txt'

function Write-Result([hashtable]`$Result) {
    `$json = `$Result | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(`$donePath, `$json, [System.Text.Encoding]::UTF8)
}

try {
    Start-Transcript -LiteralPath `$transcriptPath -Force | Out-Null
    Get-Process -Name Codex -ErrorAction SilentlyContinue | ForEach-Object {
        try { `$_.Kill(); `$_.WaitForExit(3000) } catch {} finally { `$_.Dispose() }
    }
    Get-Process -Name CodexApiProxy -ErrorAction SilentlyContinue | ForEach-Object {
        try { `$_.Kill(); `$_.WaitForExit(3000) } catch {} finally { `$_.Dispose() }
    }

    `$packageDir = Join-Path `$share 'Package'
    if (-not (Test-Path -LiteralPath `$packageDir)) { throw "Missing extracted package directory: `$packageDir" }

    `$bundleDir = Join-Path `$packageDir 'Bundle'
    `$sevenZip = Join-Path `$bundleDir 'Tools\7zip\7z.exe'
    `$desktopArchive = Join-Path `$bundleDir 'Archives\CodexDesktop.7z'
    if (-not (Test-Path -LiteralPath `$sevenZip)) { throw "Missing 7z.exe: `$sevenZip" }
    if (-not (Test-Path -LiteralPath `$desktopArchive)) { throw "Missing CodexDesktop.7z: `$desktopArchive" }

    `$installDir = Join-Path `$env:LOCALAPPDATA 'Programs\Codex'
    if (Test-Path -LiteralPath `$installDir) { Remove-Item -LiteralPath `$installDir -Recurse -Force }
    New-Item -ItemType Directory -Path `$installDir | Out-Null
    & `$sevenZip x `$desktopArchive "-o`$installDir" -y -aoa -bb0 -bso0 -bsp0
    if (`$LASTEXITCODE -ne 0) { throw "7z extraction failed: `$LASTEXITCODE" }

    Copy-Item -LiteralPath (Join-Path `$bundleDir 'Launcher') -Destination `$installDir -Recurse -Force
    Copy-Item -LiteralPath (Join-Path `$bundleDir 'Tools') -Destination `$installDir -Recurse -Force

    `$codexExe = Join-Path `$installDir 'Codex.exe'
    `$proxyExe = Join-Path `$installDir 'Launcher\CodexApiProxy.exe'
    if (-not (Test-Path -LiteralPath `$codexExe)) { throw "Installed Codex.exe not found: `$codexExe" }
    if (-not (Test-Path -LiteralPath `$proxyExe)) { throw "Installed CodexApiProxy.exe not found: `$proxyExe" }

    `$codexDir = Join-Path `$env:USERPROFILE '.codex'
    New-Item -ItemType Directory -Path `$codexDir -Force | Out-Null
    `$modelCatalog = Join-Path `$codexDir 'codex-launcher-model-catalog.json'
    `$catalogJson = @'
{
  "models": [
    { "id": "deepseek-v4-flash-free", "name": "Deepseek V4 Flash Free", "reasoning": ["none"] },
    { "id": "north-mini-code-free", "name": "North Mini Code Free", "reasoning": ["none"] },
    { "id": "mimo-v2.5-free", "name": "Mimo V2.5 Free", "reasoning": ["none"] },
    { "id": "nemotron-3-ultra-free", "name": "Nemotron 3 Ultra Free", "reasoning": ["none"] }
  ]
}
'@
    [System.IO.File]::WriteAllText(`$modelCatalog, `$catalogJson, [System.Text.Encoding]::UTF8)

    `$configPath = Join-Path `$codexDir 'config.toml'
    `$configLines = @(
        'model_provider = "custom"',
        'model = "deepseek-v4-flash-free"',
        'default_model = "deepseek-v4-flash-free"',
        'model_reasoning_effort = "none"',
        'available_models = ["deepseek-v4-flash-free", "north-mini-code-free", "mimo-v2.5-free", "nemotron-3-ultra-free"]',
        ('model_catalog_json = "' + `$modelCatalog.Replace('\', '\\') + '"'),
        'use_hidden_models = true',
        'disable_response_storage = true',
        'web_search = "disabled"',
        '',
        '[model_providers.custom]',
        'name = "free_models"',
        'base_url = "http://127.0.0.1:17631/v1"',
        'wire_api = "responses"',
        'requires_openai_auth = false'
    )
    `$configText = [string]::Join([Environment]::NewLine, `$configLines) + [Environment]::NewLine
    [System.IO.File]::WriteAllText(`$configPath, `$configText, [System.Text.Encoding]::UTF8)

    Remove-Item -LiteralPath `$netlogPath -Force -ErrorAction SilentlyContinue
    `$proxy = Start-Process -FilePath `$proxyExe -ArgumentList @('--port','17631','--upstream','https://opencode.ai/zen/v1','--codex-pid',`$PID) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 8
    `$models = Invoke-RestMethod -Uri 'http://127.0.0.1:17631/v1/models' -TimeoutSec 5

    `$codexArgs = @(
        "--log-net-log=`$netlogPath",
        '--net-log-capture-mode=Everything',
        '--remote-debugging-port=9227',
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-background-networking',
        '--disable-component-update',
        '--disable-domain-reliability',
        '--disable-sync',
        '--disable-client-side-phishing-detection',
        '--disable-features=AutofillServerCommunication,CertificateTransparencyComponentUpdater,OptimizationGuideModelDownloading,OptimizationGuideOnDeviceModel,OptimizationHints,OptimizationHintsFetching,OptimizationTargetPrediction,SegmentationPlatform,MediaRouter',
        '--host-resolver-rules=MAP chat.openai.com 0.0.0.0,MAP chatgpt.com 0.0.0.0,MAP ab.chatgpt.com 0.0.0.0,MAP a.nel.cloudflare.com 0.0.0.0,MAP android.clients.google.com 0.0.0.0,MAP clients2.google.com 0.0.0.0,MAP dl.google.com 0.0.0.0,MAP optimizationguide-pa.googleapis.com 0.0.0.0,MAP redirector.gvt1.com 0.0.0.0,MAP mtalk.google.com 0.0.0.0,EXCLUDE localhost,EXCLUDE 127.0.0.1'
    )
    `$codex = Start-Process -FilePath `$codexExe -ArgumentList `$codexArgs -PassThru
    Start-Sleep -Seconds $CaptureSeconds

    `$targets = `$null
    try { `$targets = Invoke-RestMethod -Uri 'http://127.0.0.1:9227/json' -TimeoutSec 3 } catch {}

    `$closed = `$false
    if (-not `$codex.HasExited) {
        `$closed = `$codex.CloseMainWindow()
        if (`$closed) { `$codex.WaitForExit(15000) }
    }
    if (-not `$codex.HasExited) { `$codex.Kill(); `$codex.WaitForExit(5000) }
    if (-not `$proxy.HasExited) { `$proxy.Kill(); `$proxy.WaitForExit(3000) }

    Start-Sleep -Milliseconds 1000
    `$netlogSize = 0
    if (Test-Path -LiteralPath `$netlogPath) { `$netlogSize = (Get-Item -LiteralPath `$netlogPath).Length }

    Write-Result @{
        ok = `$true
        installDir = `$installDir
        proxyModelCount = @(`$models.data).Count
        cdpTargetCount = @(`$targets).Count
        codexClosed = `$closed
        netlogPath = `$netlogPath
        netlogBytes = `$netlogSize
        timestamp = (Get-Date).ToString('o')
    }
} catch {
    [System.IO.File]::WriteAllText(`$errorPath, (`$_ | Out-String), [System.Text.Encoding]::UTF8)
    Write-Result @{ ok = `$false; error = `$_.Exception.Message; timestamp = (Get-Date).ToString('o') }
} finally {
    try { Stop-Transcript | Out-Null } catch {}
    Start-Sleep -Seconds 10
    shutdown.exe /s /t 0 /f
}
"@

[System.IO.File]::WriteAllText($guestScript, $guestScriptContent, [System.Text.Encoding]::UTF8)

$guestScriptPath = "C:\Users\WDAGUtilityAccount\Desktop\$(Split-Path -Leaf $outputDirPath)\run-in-sandbox.ps1"
$wsbContent = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$outputDirPath</HostFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <Networking>Enable</Networking>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$guestScriptPath"</Command>
  </LogonCommand>
</Configuration>
"@

[System.IO.File]::WriteAllText($wsbPath, $wsbContent, [System.Text.Encoding]::UTF8)

$process = Start-Process -FilePath $sandboxExe -ArgumentList $wsbPath -PassThru
$deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
while ([DateTimeOffset]::Now -lt $deadline) {
    if (Test-Path -LiteralPath $donePath) {
        break
    }
    Start-Sleep -Seconds 3
}

if (-not (Test-Path -LiteralPath $donePath)) {
    throw "Timed out waiting for sandbox result: $donePath"
}

$stableCount = 0
$lastSize = -1
for ($i = 0; $i -lt 30; $i++) {
    if (-not (Test-Path -LiteralPath $netlogPath)) {
        Start-Sleep -Seconds 1
        continue
    }

    $currentSize = (Get-Item -LiteralPath $netlogPath).Length
    if ($currentSize -eq $lastSize) {
        $stableCount++
        if ($stableCount -ge 3) {
            break
        }
    } else {
        $stableCount = 0
        $lastSize = $currentSize
    }

    Start-Sleep -Seconds 1
}

Get-Content -LiteralPath $donePath -Raw -Encoding UTF8
"output_dir=$outputDirPath"
"netlog=$netlogPath"
