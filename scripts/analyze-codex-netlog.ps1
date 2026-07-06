param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [int]$Top = 80
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "NetLog file not found: $LogPath"
}

function Invoke-TextFallbackAnalysis([string]$Text) {
    function Decode-JsonString([string]$Value) {
        return [Text.RegularExpressions.Regex]::Unescape($Value)
    }

    function Host-FromUrl([string]$Url) {
        if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
        try { return ([Uri]$Url).Host } catch { return $null }
    }

    $urls = [regex]::Matches($Text, '"url":"(?<url>(?:\\.|[^"\\])*)"') |
        ForEach-Object { Decode-JsonString $_.Groups['url'].Value }
    $hostsFromUrls = $urls |
        ForEach-Object { Host-FromUrl $_ } |
        Where-Object { $_ } |
        Group-Object |
        Sort-Object Count -Descending
    $hostFields = [regex]::Matches($Text, '"host":"(?<host>(?:\\.|[^"\\])*)"') |
        ForEach-Object { Decode-JsonString $_.Groups['host'].Value } |
        Where-Object { $_ } |
        Group-Object |
        Sort-Object Count -Descending
    $netErrors = [regex]::Matches($Text, '"net_error":(?<error>-?\d+)') |
        ForEach-Object { $_.Groups['error'].Value } |
        Group-Object |
        Sort-Object Count -Descending

    "=== Text Fallback: Hosts From URLs ==="
    $hostsFromUrls | Select-Object -First $Top Name, Count | Format-Table -AutoSize

    ""
    "=== Text Fallback: Host Fields ==="
    $hostFields | Select-Object -First $Top Name, Count | Format-Table -AutoSize

    ""
    "=== Text Fallback: URLs ==="
    $urls | Group-Object | Sort-Object Count -Descending | Select-Object -First $Top Name, Count | Format-Table -AutoSize -Wrap

    ""
    "=== Text Fallback: net_error Counts ==="
    $netErrors | ForEach-Object {
        $name = switch ($_.Name) {
            "0" { "OK" }
            "-3" { "ERR_ABORTED" }
            "-105" { "ERR_NAME_NOT_RESOLVED" }
            "-109" { "ERR_ADDRESS_UNREACHABLE" }
            default { "NET_ERROR_$($_.Name)" }
        }
        [pscustomobject]@{ Error = $_.Name; Name = $name; Count = $_.Count }
    } | Format-Table -AutoSize

    ""
    "url_count=$(@($urls).Count)"
    "host_from_url_count=$(@($hostsFromUrls).Count)"
    "host_field_count=$(@($hostFields).Count)"
}

$raw = Get-Content -LiteralPath $LogPath -Raw -Encoding UTF8
try {
    $json = $raw | ConvertFrom-Json -Depth 100
} catch {
    "WARN: NetLog JSON is incomplete or invalid; using text fallback. $($_.Exception.Message)"
    Invoke-TextFallbackAnalysis $raw
    return
}

$eventTypeNames = @{}
if ($json.constants.logEventTypes) {
    foreach ($property in $json.constants.logEventTypes.PSObject.Properties) {
        $eventTypeNames[[int]$property.Value] = $property.Name
    }
}

$netErrorNames = @{}
if ($json.constants.netError) {
    foreach ($property in $json.constants.netError.PSObject.Properties) {
        $netErrorNames[[int]$property.Value] = $property.Name
    }
}

function Get-PropValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-HostFromUrl([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    try {
        return ([Uri]$Url).Host
    } catch {
        return $null
    }
}

$sources = @{}
$hostCounts = @{}

foreach ($event in $json.events) {
    $source = Get-PropValue $event "source"
    $sourceId = Get-PropValue $source "id"
    if ($null -eq $sourceId) { continue }

    $key = [string]$sourceId
    if (-not $sources.ContainsKey($key)) {
        $sources[$key] = [ordered]@{
            SourceId = $key
            Url = $null
            Host = $null
            Errors = New-Object System.Collections.Generic.List[string]
            Events = New-Object System.Collections.Generic.List[string]
        }
    }

    $record = $sources[$key]
    $typeId = Get-PropValue $event "type"
    if ($null -ne $typeId) {
        $eventName = $eventTypeNames[[int]$typeId]
        if ([string]::IsNullOrWhiteSpace($eventName)) { $eventName = [string]$typeId }
        if (-not $record.Events.Contains($eventName)) {
            $record.Events.Add($eventName)
        }
    }

    $params = Get-PropValue $event "params"
    $url = Get-PropValue $params "url"
    if (-not [string]::IsNullOrWhiteSpace($url)) {
        $record.Url = [string]$url
        $host = Get-HostFromUrl $record.Url
        if ($host) {
            $record.Host = $host
            $hostCounts[$host] = 1 + ($hostCounts[$host] ?? 0)
        }
    }

    $hostParam = Get-PropValue $params "host"
    if (-not [string]::IsNullOrWhiteSpace($hostParam)) {
        $record.Host = [string]$hostParam
        $hostCounts[$record.Host] = 1 + ($hostCounts[$record.Host] ?? 0)
    }

    $netError = Get-PropValue $params "net_error"
    if ($null -ne $netError -and [int]$netError -ne 0) {
        $errorName = $netErrorNames[[int]$netError]
        if ([string]::IsNullOrWhiteSpace($errorName)) { $errorName = "NET_ERROR_$netError" }
        if (-not $record.Errors.Contains($errorName)) {
            $record.Errors.Add($errorName)
        }
    }
}

$failed = $sources.Values |
    Where-Object { $_.Errors.Count -gt 0 } |
    ForEach-Object {
        [pscustomobject]@{
            Host = $_.Host
            Url = $_.Url
            Errors = ($_.Errors -join ",")
            Events = ($_.Events -join ",")
        }
    } |
    Sort-Object Host, Url -Unique

"=== Failed Requests ==="
$failed | Select-Object -First $Top | Format-Table -AutoSize -Wrap

""
"=== Hosts Seen ==="
$hostCounts.GetEnumerator() |
    Sort-Object Value -Descending |
    Select-Object -First $Top @{Name="Host";Expression={$_.Key}}, @{Name="Events";Expression={$_.Value}} |
    Format-Table -AutoSize

""
"failed_count=$(@($failed).Count)"
"host_count=$($hostCounts.Count)"
