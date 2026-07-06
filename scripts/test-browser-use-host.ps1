param(
    [string]$InstallDir = "",
    [string]$SessionId = "",
    [string]$TurnId = "",
    [string]$ThreadId = "",
    [string]$ResultDir = "$env:USERPROFILE\Desktop\CodexBrowserUseDebug"
)

$ErrorActionPreference = "Stop"

function Resolve-InstallDir {
    param([string]$Candidate)

    if (-not [string]::IsNullOrWhiteSpace($Candidate)) {
        $full = [IO.Path]::GetFullPath($Candidate)
        if (Test-Path -LiteralPath (Join-Path $full "Codex.exe") -PathType Leaf) {
            return $full
        }
    }

    $registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexDesktopLauncher"
    if (Test-Path -LiteralPath $registryPath) {
        $registered = (Get-ItemProperty -LiteralPath $registryPath).InstallLocation
        if (-not [string]::IsNullOrWhiteSpace($registered) -and
            (Test-Path -LiteralPath (Join-Path $registered "Codex.exe") -PathType Leaf)) {
            return [IO.Path]::GetFullPath($registered)
        }
    }

    $fallbacks = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Codex"),
        (Join-Path $env:LOCALAPPDATA "Programs\CodexSandboxSmoke")
    )

    foreach ($fallback in $fallbacks) {
        if (Test-Path -LiteralPath (Join-Path $fallback "Codex.exe") -PathType Leaf) {
            return [IO.Path]::GetFullPath($fallback)
        }
    }

    throw "未找到 Codex 安装目录。可用 -InstallDir 指定。"
}

function Add-Check {
    param(
        [System.Collections.Generic.List[object]]$Checks,
        [string]$Name,
        [bool]$Ok,
        [string]$Detail = ""
    )

    $Checks.Add([pscustomobject]@{
        name = $Name
        ok = $Ok
        detail = $Detail
    })

    if ($Ok) {
        Write-Host "PASS $Name $Detail"
    } else {
        Write-Host "FAIL $Name $Detail"
    }
}

function Get-FileHashList {
    param([string[]]$Paths)

    return ($Paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    } | Select-Object -Unique) -join ","
}

$InstallDir = Resolve-InstallDir $InstallDir
$ResultDir = [IO.Path]::GetFullPath($ResultDir)
New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null

$checks = New-Object System.Collections.Generic.List[object]
$node = Join-Path $InstallDir "resources\cua_node\bin\node.exe"
$nodeRepl = Join-Path $InstallDir "resources\cua_node\bin\node_repl.exe"
$nodeModules = Join-Path $InstallDir "resources\cua_node\bin\node_modules"
$browserClient = Join-Path $InstallDir "resources\plugins\openai-bundled\plugins\browser\scripts\browser-client.mjs"
$browserClientUri = ([Uri]$browserClient).AbsoluteUri.Replace("'", "%27")

Add-Check $checks "Codex.exe exists" (Test-Path -LiteralPath (Join-Path $InstallDir "Codex.exe") -PathType Leaf) $InstallDir
Add-Check $checks "node_repl exists" (Test-Path -LiteralPath $nodeRepl -PathType Leaf) $nodeRepl
Add-Check $checks "browser client exists" (Test-Path -LiteralPath $browserClient -PathType Leaf) $browserClient

$trustedCodePaths = @(
    (Join-Path $env:USERPROFILE ".codex"),
    (Join-Path $InstallDir "resources\plugins"),
    (Join-Path $InstallDir "resources\cua_node")
) -join [IO.Path]::PathSeparator

$browserHashes = Get-FileHashList @(
    $browserClient,
    (Join-Path $InstallDir "resources\plugins\openai-bundled\plugins\chrome\scripts\browser-client.mjs"),
    (Join-Path $InstallDir "resources\cua_node\bin\node_modules\@oai\cdp-browser-backend\dist\skill\scripts\browser-client.mjs")
)
Add-Check $checks "browser client hash" (-not [string]::IsNullOrWhiteSpace($browserHashes)) ""

if ([string]::IsNullOrWhiteSpace($TurnId)) {
    $TurnId = [Guid]::NewGuid().ToString()
}

if ([string]::IsNullOrWhiteSpace($ThreadId)) {
    $ThreadId = $SessionId
}

$mcpIn = Join-Path $ResultDir "node-repl-mcp.in.jsonl"
$mcpOut = Join-Path $ResultDir "node-repl-mcp.out.txt"
$mcpErr = Join-Path $ResultDir "node-repl-mcp.err.txt"

$requests = New-Object System.Collections.Generic.List[object]
$requests.Add([ordered]@{
    jsonrpc = "2.0"
    id = 1
    method = "initialize"
    params = [ordered]@{
        protocolVersion = "2024-11-05"
        capabilities = [ordered]@{}
        clientInfo = [ordered]@{ name = "browser-use-host-debug"; version = "1.0" }
    }
})
$requests.Add([ordered]@{ jsonrpc = "2.0"; method = "notifications/initialized"; params = [ordered]@{} })
$requests.Add([ordered]@{ jsonrpc = "2.0"; id = 2; method = "tools/list"; params = [ordered]@{} })
$requests.Add([ordered]@{
    jsonrpc = "2.0"
    id = 3
    method = "tools/call"
    params = [ordered]@{
        name = "js"
        arguments = [ordered]@{
            title = "Smoke JS"
            code = 'nodeRepl.write("node-repl-js-ok")'
        }
    }
})
$requests.Add([ordered]@{
    jsonrpc = "2.0"
    id = 4
    method = "tools/call"
    params = [ordered]@{
        name = "js"
        arguments = [ordered]@{
            title = "Load Browser"
            code = "var importedBrowserClient = await import('$browserClientUri'); if (typeof importedBrowserClient.setupBrowserRuntime !== 'function') throw new Error('missing setupBrowserRuntime export'); nodeRepl.write('browser-client-load-ok');"
        }
    }
})

if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
    $requests.Add([ordered]@{
        jsonrpc = "2.0"
        id = 5
        method = "tools/call"
        params = [ordered]@{
            name = "js"
            arguments = [ordered]@{
                title = "Select In-App Browser"
                code = "var importedBrowserClient = await import('$browserClientUri'); await importedBrowserClient.setupBrowserRuntime({ globals: globalThis }); var selectedBrowser = await agent.browsers.get('iab'); nodeRepl.write('browser-iab-ok:' + (selectedBrowser?.id ?? selectedBrowser?.browserId ?? 'selected'));"
                timeout_ms = 15000
            }
            _meta = [ordered]@{
                "x-codex-turn-metadata" = [ordered]@{
                    session_id = $SessionId
                    turn_id = $TurnId
                    thread_id = $ThreadId
                    thread_source = "desktop"
                }
            }
        }
    })
}

$inputText = ($requests | ForEach-Object {
    $_ | ConvertTo-Json -Depth 20 -Compress
}) -join "`n"
$inputText += "`n"
[IO.File]::WriteAllText($mcpIn, $inputText, [Text.UTF8Encoding]::new($false))

$env:NODE_REPL_NODE_PATH = $node
$env:NODE_REPL_NODE_MODULE_DIRS = $nodeModules
$env:CODEX_HOME = Join-Path $env:USERPROFILE ".codex"
$env:NODE_REPL_TRUSTED_CODE_PATHS = $trustedCodePaths
$env:NODE_REPL_TRUSTED_BROWSER_CLIENT_SHA256S = $browserHashes
$env:BROWSER_USE_AVAILABLE_BACKENDS = "chrome,iab"
$env:BROWSER_USE_CODEX_APP_BUILD_FLAVOR = "prod"
$env:BROWSER_USE_CODEX_APP_VERSION = "26.623.81905"
$env:SKY_CUA_NATIVE_PIPE = "1"

$process = Start-Process -FilePath $nodeRepl -RedirectStandardInput $mcpIn -RedirectStandardOutput $mcpOut -RedirectStandardError $mcpErr -Wait -PassThru -WindowStyle Hidden
$mcpText = if (Test-Path -LiteralPath $mcpOut) { Get-Content -LiteralPath $mcpOut -Raw } else { "" }

Add-Check $checks "node_repl mcp handshake" ($process.ExitCode -eq 0 -and $mcpText -like '*"protocolVersion"*') "exit=$($process.ExitCode)"
Add-Check $checks "node_repl js tool exposed" ($mcpText -like '*"name":"js"*') ""
Add-Check $checks "node_repl js executes" ($mcpText -like '*node-repl-js-ok*') ""
Add-Check $checks "browser client loads" ($mcpText -like '*browser-client-load-ok*') ""
if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
    Add-Check $checks "in-app browser selected" ($mcpText -like '*browser-iab-ok:*') ""
} else {
    Write-Host "SKIP in-app browser selected: pass -SessionId to test a live Codex browser route"
}

$summary = [pscustomobject]@{
    success = -not ($checks | Where-Object { -not $_.ok } | Select-Object -First 1)
    installDir = $InstallDir
    sessionId = $SessionId
    resultDir = $ResultDir
    checks = $checks
}
$summary | ConvertTo-Json -Depth 10 | Out-File -LiteralPath (Join-Path $ResultDir "summary.json") -Encoding UTF8

if (-not $summary.success) {
    exit 1
}
