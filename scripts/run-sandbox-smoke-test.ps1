param(
    [string]$PackageDir = "$env:USERPROFILE\Desktop\CodexInstaller-LauncherSkills-CleanProxy",
    [string]$ResultDir = "$env:USERPROFILE\Desktop\CodexSandboxResults",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PackageDir)) {
    throw "PackageDir not found: $PackageDir"
}

$PackageDir = [IO.Path]::GetFullPath($PackageDir)
$ResultDir = [IO.Path]::GetFullPath($ResultDir)
$packageLeaf = Split-Path -Leaf $PackageDir
$resultLeaf = Split-Path -Leaf $ResultDir

New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null
@(
    "summary.json",
    "done.txt",
    "sandbox-smoke.log",
    "installer.log",
    "node-repl-setup.out.txt",
    "node-repl-setup.err.txt",
    "node-repl-mcp.in.jsonl",
    "node-repl-mcp.out.txt",
    "node-repl-mcp.err.txt",
    "node-repl-diagnostics.json",
    "proxy-self-test.out.txt",
    "proxy-self-test.err.txt"
) | ForEach-Object {
    Remove-Item -LiteralPath (Join-Path $ResultDir $_) -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $ResultDir -Filter "diag-*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$innerScript = @'
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$ResultDir
)

$ErrorActionPreference = "Stop"

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = "")
    $checks.Add([pscustomobject]@{
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

function Test-FileExists {
    param([string]$Name, [string]$Path)
    Add-Check $Name (Test-Path -LiteralPath $Path -PathType Leaf) $Path
}

function Test-DirExists {
    param([string]$Name, [string]$Path)
    Add-Check $Name (Test-Path -LiteralPath $Path -PathType Container) $Path
}

$logPath = Join-Path $ResultDir "sandbox-smoke.log"
Start-Transcript -Path $logPath -Force | Out-Null

$success = $false
$errorText = ""
try {
    Write-Host "PackageDir=$PackageDir"
    Write-Host "ResultDir=$ResultDir"

    $installer = Join-Path $PackageDir "Codex 安装.exe"
    Test-FileExists "installer exists" $installer

    $installDir = Join-Path $env:LOCALAPPDATA "Programs\CodexSandboxSmoke"
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue

    $installerLog = Join-Path $ResultDir "installer.log"
    $installArgs = @(
        "--silent-install",
        "`"$installDir`"",
        "--log",
        "`"$installerLog`""
    )
    $installProcess = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru
    Add-Check "silent installer exit" ($installProcess.ExitCode -eq 0) "exit=$($installProcess.ExitCode)"

    Test-FileExists "Codex.exe installed" (Join-Path $installDir "Codex.exe")
    Test-FileExists "launcher installed" (Join-Path $installDir "Launcher\Codex 启动.exe")
    Test-FileExists "api proxy installed" (Join-Path $installDir "Launcher\CodexApiProxy.exe")
    Test-FileExists "bundled node installed" (Join-Path $installDir "Tools\Node\node.exe")
    Test-FileExists "node_repl installed" (Join-Path $installDir "resources\cua_node\bin\node_repl.exe")
    Test-FileExists "cua node installed" (Join-Path $installDir "resources\cua_node\bin\node.exe")
    Test-DirExists "cua node modules installed" (Join-Path $installDir "resources\cua_node\bin\node_modules")

    $setup = Join-Path $installDir "resources\cua_node\bin\setup.ps1"
    $setupOut = Join-Path $ResultDir "node-repl-setup.out.txt"
    $setupErr = Join-Path $ResultDir "node-repl-setup.err.txt"
    $setupProcess = Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "`"$setup`""
    ) -RedirectStandardOutput $setupOut -RedirectStandardError $setupErr -Wait -PassThru -WindowStyle Hidden
    Add-Check "node_repl setup validation" ($setupProcess.ExitCode -eq 0) "exit=$($setupProcess.ExitCode)"
    if ($setupProcess.ExitCode -ne 0) {
        $diagnostics = New-Object System.Collections.Generic.List[object]
        function Invoke-DiagnosticCommand {
            param(
                [string]$Name,
                [string]$FilePath,
                [string[]]$Arguments,
                [string]$WorkingDirectory = ""
            )

            $safeName = ($Name -replace '[^a-zA-Z0-9._-]', '-')
            $diagOut = Join-Path $ResultDir "diag-$safeName.out.txt"
            $diagErr = Join-Path $ResultDir "diag-$safeName.err.txt"
            try {
                $startArgs = @{
                    FilePath = $FilePath
                    ArgumentList = $Arguments
                    RedirectStandardOutput = $diagOut
                    RedirectStandardError = $diagErr
                    Wait = $true
                    PassThru = $true
                    WindowStyle = "Hidden"
                }
                if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
                    $startArgs.WorkingDirectory = $WorkingDirectory
                }
                $diagProcess = Start-Process @startArgs
                $diagnostics.Add([pscustomobject]@{
                    name = $Name
                    exitCode = $diagProcess.ExitCode
                    stdout = if (Test-Path -LiteralPath $diagOut) { [IO.File]::ReadAllText($diagOut) } else { "" }
                    stderr = if (Test-Path -LiteralPath $diagErr) { [IO.File]::ReadAllText($diagErr) } else { "" }
                })
            } catch {
                $diagnostics.Add([pscustomobject]@{
                    name = $Name
                    exitCode = $null
                    stdout = ""
                    stderr = $_ | Out-String
                })
            }
        }

        $node = Join-Path $installDir "resources\cua_node\bin\node.exe"
        $nodeRepl = Join-Path $installDir "resources\cua_node\bin\node_repl.exe"
        $nodeBin = Join-Path $installDir "resources\cua_node\bin"
        $nodeModules = Join-Path $nodeBin "node_modules"
        Invoke-DiagnosticCommand "node version" $node @("--version")
        Invoke-DiagnosticCommand "corepack version" (Join-Path $nodeBin "corepack.cmd") @("--version")
        Invoke-DiagnosticCommand "npm version" (Join-Path $nodeBin "npm.cmd") @("--version")

        $validationDir = Join-Path ([IO.Path]::GetTempPath()) ("node-repl-diag-" + [Guid]::NewGuid().ToString("N"))
        $validationNodeModules = Join-Path $validationDir "node_modules"
        New-Item -ItemType Directory -Force -Path $validationDir | Out-Null
        New-Item -ItemType Junction -Path $validationNodeModules -Target $nodeModules | Out-Null
        try {
            Invoke-DiagnosticCommand "import oai sky" $node @(
                "--input-type=module",
                "--eval",
                "const imported = await import('@oai/sky'); if (!imported.sky) throw new Error('@oai/sky missing sky export');"
            ) $validationDir
        } finally {
            cmd.exe /c rmdir $validationNodeModules | Out-Null
            Remove-Item -LiteralPath $validationDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        Invoke-DiagnosticCommand "node repl help" $nodeRepl @("--help")
        $diagnostics | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $ResultDir "node-repl-diagnostics.json") -Encoding UTF8
    }

    $node = Join-Path $installDir "resources\cua_node\bin\node.exe"
    $nodeRepl = Join-Path $installDir "resources\cua_node\bin\node_repl.exe"
    $nodeModules = Join-Path $installDir "resources\cua_node\bin\node_modules"
    $privateCodexHome = Join-Path $installDir "Data\.codex"
    $trustedCodePaths = @(
        $privateCodexHome,
        (Join-Path $installDir "resources\plugins"),
        (Join-Path $installDir "resources\cua_node")
    ) -join [IO.Path]::PathSeparator
    $browserClientPaths = @(
        (Join-Path $installDir "resources\plugins\openai-bundled\plugins\browser\scripts\browser-client.mjs"),
        (Join-Path $installDir "resources\plugins\openai-bundled\plugins\chrome\scripts\browser-client.mjs"),
        (Join-Path $installDir "resources\cua_node\bin\node_modules\@oai\cdp-browser-backend\dist\skill\scripts\browser-client.mjs")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    $browserClientHashes = ($browserClientPaths | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    } | Select-Object -Unique) -join ","
    $browserClient = Join-Path $installDir "resources\plugins\openai-bundled\plugins\browser\scripts\browser-client.mjs"
    $browserClientForJs = ([Uri]$browserClient).AbsoluteUri.Replace("'", "%27")
    $mcpIn = Join-Path $ResultDir "node-repl-mcp.in.jsonl"
    $mcpOut = Join-Path $ResultDir "node-repl-mcp.out.txt"
    $mcpErr = Join-Path $ResultDir "node-repl-mcp.err.txt"
    $runtimeSmokeCode = "var browserRuntimeModule = await import('$browserClientForJs'); await browserRuntimeModule.setupBrowserRuntime({ globals: globalThis }); var browsers = await agent.browsers.list(); nodeRepl.write('browser-runtime-ok:' + browsers.length);"
    $mcpRequests = @(
        [ordered]@{
            jsonrpc = "2.0"
            id = 1
            method = "initialize"
            params = [ordered]@{
                protocolVersion = "2024-11-05"
                capabilities = [ordered]@{}
                clientInfo = [ordered]@{ name = "smoke"; version = "1.0" }
            }
        },
        [ordered]@{
            jsonrpc = "2.0"
            method = "notifications/initialized"
            params = [ordered]@{}
        },
        [ordered]@{
            jsonrpc = "2.0"
            id = 2
            method = "tools/list"
            params = [ordered]@{}
        },
        [ordered]@{
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
        },
        [ordered]@{
            jsonrpc = "2.0"
            id = 4
            method = "tools/call"
            params = [ordered]@{
                name = "js"
                arguments = [ordered]@{
                    title = "Load Browser"
                    code = "var browserSmokeModule = await import('$browserClientForJs'); if (typeof browserSmokeModule.setupBrowserRuntime !== 'function') throw new Error('missing setupBrowserRuntime export'); nodeRepl.write('browser-client-load-ok');"
                }
            }
        },
        [ordered]@{
            jsonrpc = "2.0"
            id = 5
            method = "tools/call"
            params = [ordered]@{
                name = "js"
                arguments = [ordered]@{
                    title = "Browser Runtime"
                    code = $runtimeSmokeCode
                    timeout_ms = 15000
                }
                _meta = [ordered]@{
                    "x-codex-turn-metadata" = [ordered]@{
                        session_id = "smoke-session"
                        turn_id = "smoke-turn"
                        thread_id = "smoke-thread"
                        thread_source = "desktop"
                    }
                }
            }
        }
    )
    $mcpInputText = ($mcpRequests | ForEach-Object {
        $_ | ConvertTo-Json -Depth 20 -Compress
    }) -join "`n"
    $mcpInputText += "`n"
    [IO.File]::WriteAllText($mcpIn, $mcpInputText, [Text.UTF8Encoding]::new($false))

    $env:NODE_REPL_NODE_PATH = $node
    $env:NODE_REPL_NODE_MODULE_DIRS = $nodeModules
    $env:CODEX_HOME = $privateCodexHome
    $env:NODE_REPL_TRUSTED_CODE_PATHS = $trustedCodePaths
    $env:NODE_REPL_TRUSTED_BROWSER_CLIENT_SHA256S = $browserClientHashes
    $env:BROWSER_USE_AVAILABLE_BACKENDS = "chrome,iab"
    $env:BROWSER_USE_CODEX_APP_BUILD_FLAVOR = "prod"
    $env:BROWSER_USE_CODEX_APP_VERSION = "26.623.81905"
    $env:SKY_CUA_NATIVE_PIPE = "1"
    $mcpProcess = Start-Process -FilePath $nodeRepl -RedirectStandardInput $mcpIn -RedirectStandardOutput $mcpOut -RedirectStandardError $mcpErr -Wait -PassThru -WindowStyle Hidden
    $mcpText = if (Test-Path -LiteralPath $mcpOut) { Get-Content -LiteralPath $mcpOut -Raw } else { "" }
    Add-Check "node_repl mcp handshake" ($mcpProcess.ExitCode -eq 0 -and $mcpText -like '*"protocolVersion"*') "exit=$($mcpProcess.ExitCode)"
    Add-Check "node_repl js tool exposed" ($mcpText -like '*"name":"js"*') ""
    Add-Check "node_repl js executes" ($mcpText -like '*node-repl-js-ok*') ""
    Add-Check "browser client script exists" (Test-Path -LiteralPath $browserClient -PathType Leaf) $browserClient
    Add-Check "browser client hash generated" (-not [string]::IsNullOrWhiteSpace($browserClientHashes)) ""
    Add-Check "browser client loads via node_repl" ($mcpText -like '*browser-client-load-ok*') ""
    Add-Check "browser runtime accepts codex metadata" ($mcpText -like '*browser-runtime-ok:*') ""

    $proxy = Join-Path $installDir "Launcher\CodexApiProxy.exe"
    $proxyOut = Join-Path $ResultDir "proxy-self-test.out.txt"
    $proxyErr = Join-Path $ResultDir "proxy-self-test.err.txt"
    $proxyProcess = Start-Process -FilePath $proxy -ArgumentList "--self-test" -RedirectStandardOutput $proxyOut -RedirectStandardError $proxyErr -Wait -PassThru -WindowStyle Hidden
    Add-Check "api proxy tool-result self-test" ($proxyProcess.ExitCode -eq 0) "exit=$($proxyProcess.ExitCode)"

    $config = Join-Path $installDir "Data\.codex\config.toml"
    Test-FileExists "private codex config exists" $config
    $configText = Get-Content -LiteralPath $config -Raw
    Add-Check "default free model configured" ($configText -like '*model = "deepseek-v4-flash-free"*') ""
    Add-Check "custom provider configured" ($configText -like '*model_provider = "custom"*' -and $configText -like '*[model_providers.custom]*') ""
    Add-Check "free models available" ($configText -like '*available_models*deepseek-v4-flash-free*') ""
    Add-Check "hidden model filter enabled" ($configText -like '*use_hidden_models = true*') ""
    Add-Check "reasoning disabled for free model" ($configText -like '*model_reasoning_effort = "none"*') ""
    Add-Check "node_repl mcp section" ($configText -like "*[mcp_servers.node_repl]*") ""
    Add-Check "node_repl points to install dir" ($configText -like "*CodexSandboxSmoke*resources*cua_node*bin*node_repl.exe*") ""
    Add-Check "no wrong mcp prefix" ($configText -notlike "*mcp__node_repl*") ""
    Add-Check "browser client trust hash" ($configText -like "*NODE_REPL_TRUSTED_BROWSER_CLIENT_SHA256S*") ""
    Add-Check "browser plugin trusted path" ($configText -like "*resources*plugins*" -and $configText -like "*resources*cua_node*") ""
    Add-Check "browser plugin enabled" ($configText -match '(?s)\[plugins\."browser@openai-bundled"\].*?enabled\s*=\s*true') ""
    Add-Check "computer use plugin enabled" ($configText -match '(?s)\[plugins\."computer-use@openai-bundled"\].*?enabled\s*=\s*true') ""
    Add-Check "hyperframes plugin enabled" ($configText -match '(?s)\[plugins\."hyperframes@openai-curated-remote"\].*?enabled\s*=\s*true') ""

    $userConfig = Join-Path $env:USERPROFILE ".codex\config.toml"
    Add-Check "user codex config not required" ($config -ne $userConfig) ""

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    Add-Check "bundled node not added to user PATH" ($userPath -notlike "*CodexSandboxSmoke*Tools*Node*") ""

    $success = -not ($checks | Where-Object { -not $_.ok } | Select-Object -First 1)
} catch {
    $errorText = $_ | Out-String
    Write-Host $errorText
} finally {
    try { Stop-Transcript | Out-Null } catch { }
    $summary = [pscustomobject]@{
        success = $success
        error = $errorText
        packageDir = $PackageDir
        resultDir = $ResultDir
        timestamp = (Get-Date).ToString("o")
        checks = $checks
    }
    $summary | ConvertTo-Json -Depth 10 | Out-File -LiteralPath (Join-Path $ResultDir "summary.json") -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $ResultDir "done.txt") -Value (Get-Date).ToString("o") -Encoding UTF8
}
'@

$innerPath = Join-Path $ResultDir "run-inside-sandbox.ps1"
Set-Content -LiteralPath $innerPath -Value $innerScript -Encoding UTF8

$sandboxPackageDir = "C:\Users\WDAGUtilityAccount\Desktop\$packageLeaf"
$sandboxResultDir = "C:\Users\WDAGUtilityAccount\Desktop\$resultLeaf"
$command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$sandboxResultDir\run-inside-sandbox.ps1`" -PackageDir `"$sandboxPackageDir`" -ResultDir `"$sandboxResultDir`""

$wsbPath = Join-Path $ResultDir "codex-smoke-test.wsb"
$xmlPackageDir = [Security.SecurityElement]::Escape($PackageDir)
$xmlResultDir = [Security.SecurityElement]::Escape($ResultDir)
$xmlCommand = [Security.SecurityElement]::Escape($command)
@"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$xmlPackageDir</HostFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$xmlResultDir</HostFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>$xmlCommand</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $wsbPath -Encoding UTF8

Write-Host "Sandbox test files prepared:"
Write-Host "  $wsbPath"
Write-Host "  $innerPath"
Write-Host "Results:"
Write-Host "  $ResultDir"

if (-not $NoLaunch) {
    Start-Process -FilePath $wsbPath
    Write-Host "Windows Sandbox started. Wait for done.txt or summary.json in $ResultDir."
}
