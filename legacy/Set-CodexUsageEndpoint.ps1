Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AppName = "CodexResetMonitor"
$ConfigDir = Join-Path $env:APPDATA $AppName
$ConfigPath = Join-Path $ConfigDir "config.json"

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Config not found. Run Setup-CodexMonitorSecrets.ps1 first."
}

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$SecureString)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

function Get-ObjectProperties {
    param($InputObject)

    if ($null -eq $InputObject) { return @() }
    if ($InputObject -is [System.Collections.IDictionary]) {
        return $InputObject.GetEnumerator() | ForEach-Object {
            [pscustomobject]@{ Name = [string]$_.Key; Value = $_.Value }
        }
    }
    return $InputObject.PSObject.Properties | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; Value = $_.Value }
    }
}

function Find-Fields {
    param(
        $InputObject,
        [string]$Pattern,
        [string]$Prefix = ""
    )

    $found = New-Object System.Collections.Generic.List[object]
    if ($null -eq $InputObject) { return $found }

    if ($InputObject -is [string] -or $InputObject.GetType().IsPrimitive) {
        return $found
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [System.Collections.IDictionary])) {
        $index = 0
        foreach ($item in $InputObject) {
            $childPrefix = if ($Prefix) { "$Prefix[$index]" } else { "[$index]" }
            $child = Find-Fields -InputObject $item -Pattern $Pattern -Prefix $childPrefix
            foreach ($entry in $child) { $found.Add($entry) }
            $index++
        }
        return $found
    }

    foreach ($property in (Get-ObjectProperties -InputObject $InputObject)) {
        $name = $property.Name
        $value = $property.Value
        $path = if ($Prefix) { "$Prefix.$name" } else { $name }
        if ($name -match $Pattern) {
            $found.Add([pscustomobject]@{ Path = $path; Name = $name; Value = $value })
        }
        if ($null -ne $value -and -not ($value -is [string]) -and -not ($value.GetType().IsPrimitive)) {
            $child = Find-Fields -InputObject $value -Pattern $Pattern -Prefix $path
            foreach ($entry in $child) { $found.Add($entry) }
        }
    }
    return $found
}

function ConvertTo-DisplayValue {
    param($Value)

    if ($null -eq $Value) { return "<null>" }
    if ($Value -is [string] -or $Value.GetType().IsPrimitive) {
        $text = [string]$Value
        if ($text.Length -gt 160) {
            return $text.Substring(0, 160) + "..."
        }
        return $text
    }
    return "<object>"
}

$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$secureToken = ConvertTo-SecureString -String $config.access_token_dpapi
$accessToken = Convert-SecureStringToPlainText -SecureString $secureToken

$endpoint = Read-Host -Prompt "Paste Codex 5-hour / weekly usage endpoint URL"
if ([string]::IsNullOrWhiteSpace($endpoint)) {
    throw "Endpoint cannot be empty."
}
if ($endpoint -notmatch "^https://chatgpt\.com/") {
    Write-Warning "This does not look like a chatgpt.com endpoint. Continue only if you trust it."
}

$headers = @{
    Authorization = "Bearer $accessToken"
    "OpenAI-Beta" = [string]$config.openai_beta
    originator = [string]$config.originator
    "ChatGPT-Account-ID" = [string]$config.account_id
}

Write-Host ""
Write-Host "Testing endpoint..."
$response = Invoke-RestMethod -Uri $endpoint -Method Get -Headers $headers -TimeoutSec 30

$fields = Find-Fields -InputObject $response -Pattern "(?i)5h|five|hour|week|weekly|usage|used|limit|remaining|reset|resets|until|expir"

Write-Host ""
Write-Host "Matched fields:"
if ($fields.Count -eq 0) {
    Write-Host "  No obvious usage/reset fields found."
}
else {
    foreach ($field in ($fields | Select-Object -First 80)) {
        Write-Host ("  {0}: {1}" -f $field.Path, (ConvertTo-DisplayValue -Value $field.Value))
    }
}

Write-Host ""
$answer = Read-Host -Prompt "Save this endpoint to config.json? Type YES to save"
if ($answer -ne "YES") {
    Write-Host "Not saved."
    exit 0
}

$config.usage_endpoint = $endpoint.Trim()
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

Write-Host ""
Write-Host "Saved usage_endpoint to:"
Write-Host $ConfigPath
Write-Host "Restart Start-CodexMonitor.ps1 or use tray menu Refresh now."
