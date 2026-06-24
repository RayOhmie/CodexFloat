$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "CodexFloat.cs"
$out = Join-Path $root "CodexFloat.exe"
$icon = Join-Path $root "assets\CodexFloat.ico"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

if (!(Test-Path $icon)) {
    & (Join-Path $root "tools\Generate-Icon.ps1")
}

& $csc /nologo /warn:0 /codepage:65001 /target:winexe /optimize+ `
    /out:$out `
    /win32icon:$icon `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    $src

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $out"
