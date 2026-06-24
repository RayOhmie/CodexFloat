Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AppName = "CodexResetMonitor"
$ConfigDir = Join-Path $env:APPDATA $AppName
$ConfigPath = Join-Path $ConfigDir "config.json"

function Read-PlainTextSecret {
    param([Parameter(Mandatory = $true)][string]$Prompt)

    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null

Write-Host ""
Write-Host "This setup stores your ACCESS_TOKEN encrypted with Windows DPAPI for the current Windows user."
Write-Host "Do not run this from an untrusted Windows account."
Write-Host ""

$accessTokenPlain = Read-PlainTextSecret -Prompt "Paste ACCESS_TOKEN (input is hidden)"
$accountId = Read-Host -Prompt "Paste ChatGPT ACCOUNT_ID"

if ([string]::IsNullOrWhiteSpace($accessTokenPlain)) {
    throw "ACCESS_TOKEN cannot be empty."
}
if ([string]::IsNullOrWhiteSpace($accountId)) {
    throw "ACCOUNT_ID cannot be empty."
}

$secureToken = ConvertTo-SecureString -String $accessTokenPlain -AsPlainText -Force
$encryptedToken = ConvertFrom-SecureString -SecureString $secureToken

$config = [ordered]@{
    access_token_dpapi = $encryptedToken
    account_id = $accountId.Trim()
    refresh_seconds = 300
    reset_cards_endpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"
    usage_endpoint = "https://chatgpt.com/backend-api/wham/usage"
    originator = "Codex Desktop"
    openai_beta = "codex-1"
}

$config | ConvertTo-Json -Depth 10 | Set-Content -Path $ConfigPath -Encoding UTF8

Write-Host ""
Write-Host "Saved encrypted config:"
Write-Host $ConfigPath
Write-Host ""
Write-Host "Next:"
Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSScriptRoot\Start-CodexMonitor.ps1`""
