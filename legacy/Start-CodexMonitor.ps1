Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$AppName = "CodexResetMonitor"
$ConfigDir = Join-Path $env:APPDATA $AppName
$ConfigPath = Join-Path $ConfigDir "config.json"

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    [System.Windows.Forms.MessageBox]::Show(
        "Config not found. Run Setup-CodexMonitorSecrets.ps1 first.",
        $AppName,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    ) | Out-Null
    exit 1
}

$script:Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$script:LatestSummary = "Loading Codex usage..."
$script:LatestDetails = "No data yet."
$script:LastRefresh = $null
$script:IsRefreshing = $false
$script:ScrollIndex = 0

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

function Get-AccessToken {
    $secure = ConvertTo-SecureString -String $script:Config.access_token_dpapi
    return Convert-SecureStringToPlainText -SecureString $secure
}

function Get-Headers {
    $token = Get-AccessToken
    return @{
        Authorization = "Bearer $token"
        "OpenAI-Beta" = [string]$script:Config.openai_beta
        originator = [string]$script:Config.originator
        "ChatGPT-Account-ID" = [string]$script:Config.account_id
    }
}

function ConvertTo-LocalDateText {
    param($Value)

    if ($null -eq $Value) { return $null }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }

    $longValue = 0L
    if ([long]::TryParse($text, [ref]$longValue)) {
        try {
            if ($longValue -gt 999999999999) {
                return ([DateTimeOffset]::FromUnixTimeMilliseconds($longValue)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            }
            if ($longValue -gt 1000000000) {
                return ([DateTimeOffset]::FromUnixTimeSeconds($longValue)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            }
        }
        catch {
            return $null
        }
    }

    try {
        return ([DateTimeOffset]::Parse($text)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
    }
    catch {
        return $null
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

function Get-NamedValue {
    param(
        $InputObject,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [System.Collections.IDictionary]) {
        foreach ($name in $Names) {
            if ($InputObject.Contains($name)) { return $InputObject[$name] }
        }
        return $null
    }
    foreach ($name in $Names) {
        $property = $InputObject.PSObject.Properties[$name]
        if ($property) { return $property.Value }
    }
    return $null
}

function ConvertTo-NumberOrNull {
    param($Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        return [double]$Value
    }
    $number = 0.0
    if ([double]::TryParse([string]$Value, [Globalization.NumberStyles]::Any, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        return $number
    }
    return $null
}

function Format-UsageWindow {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        $Window,
        [int]$DefaultSeconds
    )

    if ($null -eq $Window) {
        return [pscustomobject]@{
            Line = "${Label}: unavailable"
            Summary = "$Label --"
            Available = $false
        }
    }

    $used = ConvertTo-NumberOrNull -Value (Get-NamedValue -InputObject $Window -Names @("used_percent", "used_percentage", "utilization"))
    $remaining = ConvertTo-NumberOrNull -Value (Get-NamedValue -InputObject $Window -Names @("remaining_percent", "remaining_percentage"))
    if ($null -ne $remaining) {
        $used = 100.0 - $remaining
    }
    if ($null -ne $used -and $used -le 1.0) {
        $used = $used * 100.0
    }
    if ($null -ne $used) {
        $used = [Math]::Max(0.0, [Math]::Min(100.0, $used))
    }

    $resetRaw = Get-NamedValue -InputObject $Window -Names @("reset_at", "resets_at", "reset_time", "expires_at")
    $resetText = ConvertTo-LocalDateText -Value $resetRaw
    if (-not $resetText) {
        $afterSeconds = ConvertTo-NumberOrNull -Value (Get-NamedValue -InputObject $Window -Names @("reset_after_seconds"))
        if ($null -ne $afterSeconds) {
            $resetText = (Get-Date).AddSeconds([Math]::Max(0, $afterSeconds)).ToString("yyyy-MM-dd HH:mm:ss zzz")
        }
    }

    $windowSeconds = ConvertTo-NumberOrNull -Value (Get-NamedValue -InputObject $Window -Names @("limit_window_seconds"))
    $windowMinutes = ConvertTo-NumberOrNull -Value (Get-NamedValue -InputObject $Window -Names @("window_minutes"))
    if ($null -eq $windowSeconds -and $null -ne $windowMinutes) {
        $windowSeconds = $windowMinutes * 60
    }
    if ($null -eq $windowSeconds) {
        $windowSeconds = $DefaultSeconds
    }

    $remainingPct = if ($null -ne $used) { 100.0 - $used } else { $null }
    $remainingText = if ($null -ne $remainingPct) { "{0:N1}%" -f $remainingPct } else { "--" }
    $usedText = if ($null -ne $used) { "{0:N1}%" -f $used } else { "--" }
    $resetShort = if ($resetText) { $resetText.Substring(0, [Math]::Min(16, $resetText.Length)) } else { "--" }
    $hours = [Math]::Round($windowSeconds / 3600.0, 1)

    return [pscustomobject]@{
        Line = "${Label}: remaining $remainingText (used $usedText), reset $resetText, window ${hours}h"
        Summary = "$Label remaining $remainingText reset $resetShort"
        Available = $true
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

function Invoke-CodexEndpoint {
    param([Parameter(Mandatory = $true)][string]$Uri)

    return Invoke-RestMethod -Uri $Uri -Method Get -Headers (Get-Headers) -TimeoutSec 30
}

function Format-ResetCards {
    param($Response)

    $dateFields = Find-Fields -InputObject $Response -Pattern "(?i)expir|valid.*until|valid_until|expires_at|expiry"
    $dates = New-Object System.Collections.Generic.List[string]
    foreach ($field in $dateFields) {
        $converted = ConvertTo-LocalDateText -Value $field.Value
        if ($converted -and -not $dates.Contains($converted)) {
            $dates.Add($converted)
        }
    }

    $countFields = Find-Fields -InputObject $Response -Pattern "(?i)^count$|remaining|available|quantity|total"
    $countText = ""
    $firstCount = $countFields | Select-Object -First 1
    if ($firstCount) {
        $countText = "Count hint: $($firstCount.Path) = $($firstCount.Value)"
    }

    if ($dates.Count -eq 0) {
        return [pscustomobject]@{
            Summary = "Reset cards: no expiry fields found"
            Details = "Reset card response did not expose an obvious expiry field.`r`n$countText"
            Dates = @()
        }
    }

    $summaryDates = $dates | ForEach-Object { $_.Substring(0, [Math]::Min(16, $_.Length)) }
    return [pscustomobject]@{
        Summary = "Reset cards: $($dates.Count) expiry time(s): " + ($summaryDates -join " / ")
        Details = "Reset card expiry times:`r`n- " + ($dates -join "`r`n- ") + ($(if ($countText) { "`r`n`r`n$countText" } else { "" }))
        Dates = $dates
    }
}

function Format-Usage {
    param($Response)

    if ($null -eq $Response) {
        return [pscustomobject]@{
            Summary = "Usage endpoint not configured"
            Details = "Set usage_endpoint in $ConfigPath. Common Codex tools use https://chatgpt.com/backend-api/wham/usage."
        }
    }

    $source = Get-NamedValue -InputObject $Response -Names @("rate_limit_status")
    if ($null -eq $source) { $source = $Response }
    $rateLimit = Get-NamedValue -InputObject $source -Names @("rate_limit")
    if ($null -eq $rateLimit) { $rateLimit = Get-NamedValue -InputObject $Response -Names @("rate_limit") }

    if ($null -ne $rateLimit) {
        $primary = Get-NamedValue -InputObject $rateLimit -Names @("primary_window", "primary")
        $secondary = Get-NamedValue -InputObject $rateLimit -Names @("secondary_window", "secondary")
        $fiveHour = Format-UsageWindow -Label "5h" -Window $primary -DefaultSeconds 18000
        $weekly = Format-UsageWindow -Label "1w" -Window $secondary -DefaultSeconds 604800
        $plan = Get-NamedValue -InputObject $Response -Names @("plan_type", "plan")
        $credits = Get-NamedValue -InputObject $Response -Names @("credits")
        $creditBalance = Get-NamedValue -InputObject $credits -Names @("balance")
        $additional = Get-NamedValue -InputObject $Response -Names @("additional_rate_limits")

        $detailLines = New-Object System.Collections.Generic.List[string]
        if ($plan) { $detailLines.Add("Plan: $plan") }
        $detailLines.Add($fiveHour.Line)
        $detailLines.Add($weekly.Line)
        if ($creditBalance) { $detailLines.Add("Credits balance: $creditBalance") }
        if ($additional) { $detailLines.Add("Additional rate limits present: yes") }

        return [pscustomobject]@{
            Summary = "Usage: $($fiveHour.Summary) | $($weekly.Summary)"
            Details = "Codex usage endpoint:`r`n- " + ($detailLines -join "`r`n- ")
        }
    }

    $fields = Find-Fields -InputObject $Response -Pattern "(?i)5h|five|hour|week|weekly|usage|used|limit|remaining|reset|resets|until|expir"
    if ($fields.Count -eq 0) {
        return [pscustomobject]@{
            Summary = "Usage: response received, no known fields"
            Details = "Usage response did not expose obvious usage/reset fields."
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($field in ($fields | Select-Object -First 80)) {
        $value = $field.Value
        $converted = ConvertTo-LocalDateText -Value $value
        if ($converted) { $value = $converted }
        if ($null -ne $value -and -not ($value -is [string]) -and -not ($value.GetType().IsPrimitive)) {
            $value = "<object>"
        }
        $lines.Add("$($field.Path): $value")
    }

    $summaryParts = $lines | Select-Object -First 4
    return [pscustomobject]@{
        Summary = "Usage: " + ($summaryParts -join " | ")
        Details = "Usage fields:`r`n- " + ($lines -join "`r`n- ")
    }
}

function Refresh-CodexData {
    if ($script:IsRefreshing) { return }
    $script:IsRefreshing = $true

    try {
        $resetResponse = Invoke-CodexEndpoint -Uri ([string]$script:Config.reset_cards_endpoint)
        $reset = Format-ResetCards -Response $resetResponse

        $usage = $null
        if ($script:Config.PSObject.Properties.Name -contains "usage_endpoint" -and -not [string]::IsNullOrWhiteSpace([string]$script:Config.usage_endpoint)) {
            $usageResponse = Invoke-CodexEndpoint -Uri ([string]$script:Config.usage_endpoint)
            $usage = Format-Usage -Response $usageResponse
        }
        else {
            $usage = Format-Usage -Response $null
        }

        $script:LastRefresh = Get-Date
        $script:LatestSummary = "$($reset.Summary)    $($usage.Summary)"
        $script:LatestDetails = @(
            "Last refresh: $($script:LastRefresh.ToString("yyyy-MM-dd HH:mm:ss"))"
            ""
            $reset.Details
            ""
            $usage.Details
        ) -join "`r`n"
        $script:ScrollIndex = 0
    }
    catch {
        $script:LatestSummary = "Codex monitor error: $($_.Exception.Message)"
        $script:LatestDetails = $_ | Out-String
    }
    finally {
        $script:IsRefreshing = $false
    }
}

function Show-DetailsWindow {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Codex Usage Details"
    $form.Size = New-Object System.Drawing.Size(760, 480)
    $form.StartPosition = "CenterScreen"
    $form.TopMost = $true

    $text = New-Object System.Windows.Forms.TextBox
    $text.Multiline = $true
    $text.ReadOnly = $true
    $text.ScrollBars = "Vertical"
    $text.Dock = "Fill"
    $text.Font = New-Object System.Drawing.Font("Consolas", 10)
    $text.Text = $script:LatestDetails

    $buttonPanel = New-Object System.Windows.Forms.Panel
    $buttonPanel.Dock = "Bottom"
    $buttonPanel.Height = 44

    $refresh = New-Object System.Windows.Forms.Button
    $refresh.Text = "Refresh"
    $refresh.Width = 100
    $refresh.Height = 28
    $refresh.Left = 12
    $refresh.Top = 8
    $refresh.Add_Click({
        Refresh-CodexData
        $text.Text = $script:LatestDetails
    })

    $openConfig = New-Object System.Windows.Forms.Button
    $openConfig.Text = "Open config"
    $openConfig.Width = 110
    $openConfig.Height = 28
    $openConfig.Left = 124
    $openConfig.Top = 8
    $openConfig.Add_Click({
        Start-Process notepad.exe $ConfigPath
    })

    $buttonPanel.Controls.Add($refresh)
    $buttonPanel.Controls.Add($openConfig)
    $form.Controls.Add($text)
    $form.Controls.Add($buttonPanel)
    $form.ShowDialog() | Out-Null
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$floatForm = New-Object System.Windows.Forms.Form
$floatForm.FormBorderStyle = "None"
$floatForm.ShowInTaskbar = $false
$floatForm.TopMost = $true
$floatForm.BackColor = [System.Drawing.Color]::FromArgb(28, 30, 34)
$floatForm.ForeColor = [System.Drawing.Color]::White
$floatForm.Size = New-Object System.Drawing.Size(520, 38)
$floatForm.StartPosition = "Manual"
$floatForm.Location = New-Object System.Drawing.Point(($screen.Right - 530), ($screen.Bottom - 46))

$label = New-Object System.Windows.Forms.Label
$label.Dock = "Fill"
$label.TextAlign = "MiddleLeft"
$label.Padding = New-Object System.Windows.Forms.Padding(12, 0, 8, 0)
$label.Font = New-Object System.Drawing.Font("Segoe UI", 10)
$label.Text = $script:LatestSummary
$floatForm.Controls.Add($label)
$floatForm.Add_Click({ Show-DetailsWindow })
$label.Add_Click({ Show-DetailsWindow })

$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Text = "Codex Reset Monitor"
$notify.Icon = [System.Drawing.SystemIcons]::Information
$notify.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$showItem = $menu.Items.Add("Show details")
$refreshItem = $menu.Items.Add("Refresh now")
$toggleItem = $menu.Items.Add("Hide floating window")
$setupItem = $menu.Items.Add("Re-run setup")
$exitItem = $menu.Items.Add("Exit")

$showItem.Add_Click({ Show-DetailsWindow })
$refreshItem.Add_Click({
    Refresh-CodexData
    $label.Text = $script:LatestSummary
})
$toggleItem.Add_Click({
    $floatForm.Visible = -not $floatForm.Visible
    $toggleItem.Text = if ($floatForm.Visible) { "Hide floating window" } else { "Show floating window" }
})
$setupItem.Add_Click({
    Start-Process powershell.exe "-ExecutionPolicy Bypass -File `"$PSScriptRoot\Setup-CodexMonitorSecrets.ps1`""
})
$exitItem.Add_Click({
    $notify.Visible = $false
    $notify.Dispose()
    $floatForm.Close()
    [System.Windows.Forms.Application]::Exit()
})
$notify.ContextMenuStrip = $menu
$notify.Add_DoubleClick({ Show-DetailsWindow })

$refreshTimer = New-Object System.Windows.Forms.Timer
$refreshTimer.Interval = [Math]::Max(60, [int]$script:Config.refresh_seconds) * 1000
$refreshTimer.Add_Tick({
    Refresh-CodexData
    $label.Text = $script:LatestSummary
})

$scrollTimer = New-Object System.Windows.Forms.Timer
$scrollTimer.Interval = 700
$scrollTimer.Add_Tick({
    $text = [string]$script:LatestSummary
    if ($text.Length -le 70) {
        $label.Text = $text
        return
    }
    $padded = "$text     "
    if ($script:ScrollIndex -ge $padded.Length) { $script:ScrollIndex = 0 }
    $rotated = $padded.Substring($script:ScrollIndex) + $padded.Substring(0, $script:ScrollIndex)
    $label.Text = $rotated.Substring(0, [Math]::Min(95, $rotated.Length))
    $script:ScrollIndex++
})

Refresh-CodexData
$label.Text = $script:LatestSummary
$refreshTimer.Start()
$scrollTimer.Start()

[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::Run($floatForm)
