param(
    [Parameter(Mandatory = $true)]
    [string]$AppDir
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [string]$WorkingDirectory = (Get-Location).Path
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Update-FileText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Old,
        [Parameter(Mandatory = $true)]
        [string]$New,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return 0
    }

    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($null -eq $text) {
        return 0
    }
    if (!$text.Contains($Old)) {
        return 0
    }

    $updated = $text.Replace($Old, $New)
    if ($updated -ne $text) {
        Set-Content -LiteralPath $Path -Value $updated -Encoding UTF8 -NoNewline
        Write-Host "  PATCHED: $Label -> $(Split-Path $Path -Leaf)" -ForegroundColor DarkGray
        return 1
    }

    return 0
}

function Update-FirstRegex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Pattern,
        [Parameter(Mandatory = $true)]
        [string]$Replacement,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return 0
    }

    $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($null -eq $text) {
        return 0
    }
    $regex = [regex]::new($Pattern)
    if (!$regex.IsMatch($text)) {
        return 0
    }

    $updated = $regex.Replace($text, $Replacement, 1)
    if ($updated -ne $text) {
        Set-Content -LiteralPath $Path -Value $updated -Encoding UTF8 -NoNewline
        Write-Host "  PATCHED: $Label -> $(Split-Path $Path -Leaf)" -ForegroundColor DarkGray
        return 1
    }

    return 0
}

function Update-PackageJson {
    $packageJsonPath = Join-Path $appFolder "package.json"
    if (!(Test-Path -LiteralPath $packageJsonPath)) {
        return 0
    }

    $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $changed = 0
    if ($null -eq $packageJson.PSObject.Properties["codexSparkleFeedUrl"]) {
        $packageJson | Add-Member -NotePropertyName "codexSparkleFeedUrl" -NotePropertyValue ""
        $changed = 1
    }
    elseif ($packageJson.codexSparkleFeedUrl -ne "") {
        $packageJson.codexSparkleFeedUrl = ""
        $changed = 1
    }
    if ($null -eq $packageJson.PSObject.Properties["codexSparklePublicKey"]) {
        $packageJson | Add-Member -NotePropertyName "codexSparklePublicKey" -NotePropertyValue ""
        $changed = 1
    }
    elseif ($packageJson.codexSparklePublicKey -ne "") {
        $packageJson.codexSparklePublicKey = ""
        $changed = 1
    }

    if ($changed) {
        $packageJson | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $packageJsonPath -Encoding UTF8
        Write-Host "  PATCHED: disable Sparkle update feed -> package.json" -ForegroundColor DarkGray
    }

    return $changed
}

$resolvedAppDir = (Resolve-Path -LiteralPath $AppDir).Path
$resourcesDir = Join-Path $resolvedAppDir "resources"
$asarPath = Join-Path $resourcesDir "app.asar"
$appFolder = Join-Path $resourcesDir "app"
$assetsDir = Join-Path $appFolder "webview\assets"
$viteBuildDir = Join-Path $appFolder ".vite\build"

if (!(Test-Path -LiteralPath $resourcesDir)) {
    throw "Codex resources directory not found: $resourcesDir"
}

if (Test-Path -LiteralPath $asarPath) {
    Remove-Item -LiteralPath $appFolder -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked -FilePath "npx" -Arguments @("--yes", "@electron/asar", "extract", $asarPath, $appFolder) -WorkingDirectory $resourcesDir
    Write-Host "  OK: extracted app.asar for patching" -ForegroundColor DarkGray
}

$jsRootDir = $appFolder

$patchCount = 0
$modelListPatchCount = 0
$i18nPatchCount = 0

$patchCount += Update-PackageJson

Get-ChildItem -LiteralPath $appFolder -Recurse -File -Include "*.js", "*.cjs", "*.mjs" | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'enableUpdater:n.i.shouldIncludeUpdater(a,process.platform,process.env)' `
        'enableUpdater:!1' `
        "disable app updater"
    $patchCount += Update-FileText $_.FullName `
        'enableSparkle:!0' `
        'enableSparkle:!1' `
        "disable Sparkle UI"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "read-service-tier-for-request-*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'return n===`chatgpt`?(await e.query.fetch(g,{authMethod:n,hostId:t})).requirements?.featureRequirements?.fast_mode!==!1:!1' `
        'return (await e.query.fetch(g,{authMethod:n,hostId:t})).requirements?.featureRequirements?.fast_mode!==!1' `
        "fast mode request gate"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "use-service-tier-settings-*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        's=a?.authMethod===`chatgpt`' `
        's=true' `
        "fast mode settings gate"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "use-is-plugins-enabled-*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'function R({areRequiredFeaturesEnabled:e,enabled:t,isAnyFeatureLoading:n,isComputerUseGateEnabled:r,isHostCompatiblePlatform:i,isPlatformLoading:a,windowType:o}){return t?o===`electron`?r?a?`loading`:i?n?`loading`:e?`available`:`config-requirement-disabled`:`unsupported-platform`:`statsig-disabled`:`window-type-disabled`:`disabled`}' `
        'function R({areRequiredFeaturesEnabled:e,enabled:t,isAnyFeatureLoading:n,isComputerUseGateEnabled:r,isHostCompatiblePlatform:i,isPlatformLoading:a,windowType:o}){return t?`available`:`disabled`}' `
        "plugins availability gate"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "use-plugin-install-flow-*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        '(r||n!=null&&!n.isPending&&n.error==null&&n.data==null)&&(i=`connector-unavailable`)' `
        'false&&(i=`connector-unavailable`)' `
        "connector availability item gate"
    $patchCount += Update-FileText $_.FullName `
        '!v&&y.length>0&&ne===y.length&&(k=D?`disabled-by-admin`:`connector-unavailable`)' `
        '!v&&y.length>0&&ne===y.length&&D&&(k=`disabled-by-admin`)' `
        "connector availability plugin gate"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'function Jm({authMethod:e,email:t,plan:n}){return e===`apikey`?!0:e===`chatgpt`?Ym({email:t,plan:n}):!1}' `
        'function Jm({authMethod:e,email:t,plan:n}){return e===`apikey`?!1:e===`chatgpt`?Ym({email:t,plan:n}):!1}' `
        "apikey plugin gate"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "app-main-*.js" -Recurse | ForEach-Object {
    $i18nPatchCount += Update-FileText $_.FullName `
        'let s=o,c=a?.get(`locale_source`,`IDE`)' `
        'let s=!0,c=a?.get(`locale_source`,`IDE`)' `
        "i18n message loading gate"
}
$patchCount += $i18nPatchCount

if ($i18nPatchCount -eq 0) {
    $hasI18nPatch = Get-ChildItem -LiteralPath $jsRootDir -File -Filter "app-main-*.js" -Recurse |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8).Contains('let s=!0,c=a?.get(`locale_source`,`IDE`)') } |
        Select-Object -First 1

    if (-not $hasI18nPatch) {
        throw "Codex i18n patch was not applied. The bundled Codex i18n provider may have changed."
    }
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "model-list-filter-*.js" -Recurse | ForEach-Object {
    $modelListPatchCount += Update-FileText $_.FullName `
        'let c=[],l=null,u=s&&e!==`amazonBedrock`,d=o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`max`)),f=a&&o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`ultra`));return o.forEach(r=>{if(u?n.has(r.model):!r.hidden){let n=a?r.supportedReasoningEfforts:r.supportedReasoningEfforts.filter(({reasoningEffort:e})=>e!==`ultra`)' `
        'let c=[],l=null,u=s&&e!==`amazonBedrock`,p=e=>String(e).endsWith(`-free`),m=p(r),d=o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`max`)),f=a&&o.some(e=>e.supportedReasoningEfforts.some(({reasoningEffort:e})=>e===`ultra`));return o.forEach(r=>{if(m?!p(r.model):p(r.model))return;if(u?n.has(r.model):!r.hidden){let n=m?[{reasoningEffort:`none`,description:`Disable Thinking`}]:a?r.supportedReasoningEfforts:r.supportedReasoningEfforts.filter(({reasoningEffort:e})=>e!==`ultra`)' `
        "free model mode gate"
    $modelListPatchCount += Update-FileText $_.FullName `
        'u&&n.forEach(e=>{c.some(t=>t.model===e)||c.push({model:e,name:e,displayName:e,isDefault:e===r,hidden:!1,defaultReasoningEffort:`none`,supportedReasoningEfforts:[`none`,`low`,`medium`,`high`,`xhigh`].filter(e=>i.has(e)).map(e=>({reasoningEffort:e,description:e===`none`?`Disable Thinking`:`${e} effort`}))})}),l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:d,hasModelSupportingUltraReasoningEffort:f}' `
        'm&&u&&[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`].forEach(e=>{!c.some(t=>t.model===e)&&c.push({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===r,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})}),l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:m?!1:d,hasModelSupportingUltraReasoningEffort:m?!1:f}' `
        "free model fixed catalog"
    $modelListPatchCount += Update-FileText $_.FullName `
        'l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:d,hasModelSupportingUltraReasoningEffort:f}' `
        'm&&u&&[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`].forEach(e=>{!c.some(t=>t.model===e)&&c.push({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===r,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})}),l??=c.find(e=>e.model===r)??null,{models:c,defaultModel:l,hasModelSupportingMaxReasoningEffort:m?!1:d,hasModelSupportingUltraReasoningEffort:m?!1:f}' `
        "free model fixed catalog"
}
$patchCount += $modelListPatchCount

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "model-and-reasoning-dropdown-*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'let A=k,j=ne===void 0?!1:ne,de=ie===void 0?!0:ie,M=ae===void 0?!1:ae,N=fe(a,i),P=pe(x,N),' `
        'let __codexFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexIsFree=e=>__codexFreeModels.includes(String(e)),__codexLabel=e=>String(e).split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),__codexFree=__codexIsFree(i)||a?.some(e=>__codexIsFree(e?.model));__codexFree&&(i=__codexIsFree(i)?i:__codexFreeModels[0],a=__codexFreeModels.map(e=>({model:e,displayName:__codexLabel(e),description:__codexLabel(e),hidden:!1,isDefault:e===i,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})),x=`none`,S=!0,le=!0);let A=k,j=ne===void 0?!1:ne,de=__codexFree?!1:(ie===void 0?!0:ie),M=__codexFree?!1:(ae===void 0?!1:ae),N=fe(a,i),P=pe(x,N),' `
        "free model dropdown"
}

Get-ChildItem -LiteralPath $jsRootDir -File -Filter "composer-*.js" -Recurse | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model;' `
        'c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model,__codexComposerFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexComposerIsFree=e=>__codexComposerFreeModels.includes(String(e)),__codexComposerFree=__codexComposerIsFree(f)||c?.some(e=>__codexComposerIsFree(e?.model));__codexComposerFree&&(f=__codexComposerIsFree(f)?f:__codexComposerFreeModels[0],u={...u,model:f,reasoningEffort:`none`},c=__codexComposerFreeModels.map(e=>({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===f,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})));' `
        "free model composer label"
}

$modelDropdownPatchCount = 0
Get-ChildItem -LiteralPath $jsRootDir -File -Include "*.js", "*.cjs", "*.mjs" -Recurse | Where-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    $null -ne $text -and $text.Contains('let A=k,j=ne===void 0?!1:ne,de=ie===void 0?!0:ie,M=ae===void 0?!1:ae,N=fe(a,i),P=pe(x,N),')
} | ForEach-Object {
    $modelDropdownPatchCount += Update-FileText $_.FullName `
        'let A=k,j=ne===void 0?!1:ne,de=ie===void 0?!0:ie,M=ae===void 0?!1:ae,N=fe(a,i),P=pe(x,N),' `
        'let __codexFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexIsFree=e=>__codexFreeModels.includes(String(e)),__codexLabel=e=>String(e).split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),__codexFree=__codexIsFree(i)||a?.some(e=>__codexIsFree(e?.model));__codexFree&&(i=__codexIsFree(i)?i:__codexFreeModels[0],a=__codexFreeModels.map(e=>({model:e,displayName:__codexLabel(e),description:__codexLabel(e),hidden:!1,isDefault:e===i,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})),x=`none`,S=!0,le=!0);let A=k,j=ne===void 0?!1:ne,de=__codexFree?!1:(ie===void 0?!0:ie),M=__codexFree?!1:(ae===void 0?!1:ae),N=fe(a,i),P=pe(x,N),' `
        "free model dropdown"
}
Get-ChildItem -LiteralPath $jsRootDir -File -Include "*.js", "*.cjs", "*.mjs" -Recurse | Where-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    $null -ne $text -and $text.Contains('let k=O,A=b===void 0?!1:b,j=x===void 0?!0:x,M=S===void 0?!1:S,N=Jlt(a,i),P=Ylt(h,N),')
} | ForEach-Object {
    $modelDropdownPatchCount += Update-FileText $_.FullName `
        'let k=O,A=b===void 0?!1:b,j=x===void 0?!0:x,M=S===void 0?!1:S,N=Jlt(a,i),P=Ylt(h,N),' `
        'let __codexFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexIsFree=e=>__codexFreeModels.includes(String(e)),__codexLabel=e=>String(e).split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),__codexFree=__codexIsFree(i)||a?.some(e=>__codexIsFree(e?.model));__codexFree&&(i=__codexIsFree(i)?i:__codexFreeModels[0],a=__codexFreeModels.map(e=>({model:e,displayName:__codexLabel(e),description:__codexLabel(e),hidden:!1,isDefault:e===i,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})),h=`none`,x=!0,S=!1);let k=O,A=b===void 0?!1:b,j=__codexFree?!1:(x===void 0?!0:x),M=__codexFree?!1:(S===void 0?!1:S),N=Jlt(a,i),P=Ylt(h,N),' `
        "free model dropdown"
}
$patchCount += $modelDropdownPatchCount

$composerPatchCount = 0
Get-ChildItem -LiteralPath $jsRootDir -File -Include "*.js", "*.cjs", "*.mjs" -Recurse | Where-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    $null -ne $text -and $text.Contains('c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model;')
} | ForEach-Object {
    $composerPatchCount += Update-FileText $_.FullName `
        'c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model;' `
        'c=o?.models,{modelSettings:u,setModelAndReasoningEffort:d}=ja(e),f=u.model,__codexComposerFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexComposerIsFree=e=>__codexComposerFreeModels.includes(String(e)),__codexComposerFree=__codexComposerIsFree(f)||c?.some(e=>__codexComposerIsFree(e?.model));__codexComposerFree&&(f=__codexComposerIsFree(f)?f:__codexComposerFreeModels[0],u={...u,model:f,reasoningEffort:`none`},c=__codexComposerFreeModels.map(e=>({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===f,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})));' `
        "free model composer label"
}
Get-ChildItem -LiteralPath $jsRootDir -File -Include "*.js", "*.cjs", "*.mjs" -Recurse | Where-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    $null -ne $text -and $text.Contains('{data:o,status:s}=$s({hostId:r.hostId}),c=o?.models,{modelSettings:l,setModelAndReasoningEffort:u}=Nn(e),d=l.model;')
} | ForEach-Object {
    $composerPatchCount += Update-FileText $_.FullName `
        '{data:o,status:s}=$s({hostId:r.hostId}),c=o?.models,{modelSettings:l,setModelAndReasoningEffort:u}=Nn(e),d=l.model;' `
        '{data:o,status:s}=$s({hostId:r.hostId}),c=o?.models,{modelSettings:l,setModelAndReasoningEffort:u}=Nn(e),d=l.model,__codexComposerFreeModels=[`deepseek-v4-flash-free`,`north-mini-code-free`,`mimo-v2.5-free`,`nemotron-3-ultra-free`],__codexComposerIsFree=e=>__codexComposerFreeModels.includes(String(e)),__codexComposerFree=__codexComposerIsFree(d)||c?.some(e=>__codexComposerIsFree(e?.model));__codexComposerFree&&(d=__codexComposerIsFree(d)?d:__codexComposerFreeModels[0],l={...l,model:d,reasoningEffort:`none`},c=__codexComposerFreeModels.map(e=>({model:e,name:e,displayName:e.split(`-`).filter(Boolean).map(e=>e.length<=3?e.toUpperCase():`${e[0]?.toUpperCase()??``}${e.slice(1)}`).join(` `),description:e,hidden:!1,isDefault:e===d,defaultReasoningEffort:`none`,supportedReasoningEfforts:[{reasoningEffort:`none`,description:`Disable Thinking`}]})));' `
        "free model composer label"
}
$patchCount += $composerPatchCount

Get-ChildItem -LiteralPath (Join-Path $resourcesDir "plugins\openai-bundled\plugins") -Recurse -File -Filter "SKILL.md" -ErrorAction SilentlyContinue | ForEach-Object {
    $patchCount += Update-FileText $_.FullName `
        'The `browser-client` module is the core entry point for browser use, and is available under `scripts/browser-client.mjs` in this plugin''s root directory. ALWAYS import it using an absolute path.' `
        'The `browser-client` module is the core entry point for browser use, and is available under `scripts/browser-client.mjs` in this plugin''s root directory. ALWAYS import it using an absolute path. On Windows, use a `file:///C:/.../browser-client.mjs` URL or a forward-slash absolute path in the dynamic import string; raw backslashes are not valid JavaScript import specifiers.' `
        "browser client import guidance"
}

if ($modelListPatchCount -eq 0) {
    $hasFreeModelPatch = Get-ChildItem -LiteralPath $jsRootDir -File -Filter "model-list-filter-*.js" -Recurse |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8).Contains('p=e=>String(e).endsWith(`-free`)') } |
        Select-Object -First 1

    if (-not $hasFreeModelPatch) {
        throw "Codex model list patch was not applied. The bundled Codex model selector may have changed."
    }
}

$hasDropdownPatch = Get-ChildItem -LiteralPath $jsRootDir -File -Include "*.js", "*.cjs", "*.mjs" -Recurse |
    Where-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        $null -ne $text -and $text.Contains("__codexFreeModels")
    } |
    Select-Object -First 1

if (-not $hasDropdownPatch) {
    throw "Codex model dropdown patch was not applied. The bundled Codex model selector may have changed."
}

$hasComposerPatch = Get-ChildItem -LiteralPath $jsRootDir -File -Include "*.js", "*.cjs", "*.mjs" -Recurse |
    Where-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        $null -ne $text -and $text.Contains("__codexComposerFreeModels")
    } |
    Select-Object -First 1

if (-not $hasComposerPatch) {
    throw "Codex composer model patch was not applied. The bundled Codex composer may have changed."
}

if ($patchCount -eq 0) {
    $hasPatchMarker = Test-Path -LiteralPath (Join-Path $resourcesDir "codex-installer-patch.txt")
    if (-not $hasPatchMarker) {
        throw "No Codex app patches were applied. The bundled Codex version may have changed."
    }
}

$packageJsonPath = Join-Path $appFolder "package.json"
if (Test-Path -LiteralPath $packageJsonPath) {
    $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($packageJson.codexSparkleFeedUrl -or $packageJson.codexSparklePublicKey) {
        throw "Codex auto-update feed was not disabled."
    }
}

Get-ChildItem -LiteralPath $appFolder -Recurse -File -Include "*.js", "*.cjs", "*.mjs" | ForEach-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
    if ($null -eq $text) {
        return
    }
    if ($text.Contains('enableUpdater:n.i.shouldIncludeUpdater(a,process.platform,process.env)') -or $text.Contains('enableSparkle:!0')) {
        throw "Codex updater patch was not applied: $($_.FullName)"
    }
}

Set-Content -LiteralPath (Join-Path $appFolder "codex-installer-patch.txt") -Value "Codex API mode fast/plugins/i18n/updater patch applied." -Encoding UTF8 -NoNewline
Remove-Item -LiteralPath $asarPath -Force -ErrorAction SilentlyContinue
Invoke-Checked -FilePath "npx" -Arguments @("--yes", "@electron/asar", "pack", $appFolder, $asarPath) -WorkingDirectory $resourcesDir
Remove-Item -LiteralPath $appFolder -Recurse -Force
Set-Content -LiteralPath (Join-Path $resourcesDir "codex-installer-patch.txt") -Value "Codex API mode fast/plugins/i18n/updater patch applied." -Encoding UTF8 -NoNewline
Write-Host "  OK: Codex app patch applied and repacked ($patchCount changes)" -ForegroundColor Green
