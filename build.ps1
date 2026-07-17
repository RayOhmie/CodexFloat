$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "CodexFloat.cs"
$out = Join-Path $root "CodexFloat.exe"
$icon = Join-Path $root "assets\CodexFloat.ico"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$gac = "$env:WINDIR\Microsoft.NET\assembly"
$systemXaml = Join-Path $gac "GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
$windowsBase = Join-Path $gac "GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll"
$presentationCore = Join-Path $gac "GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll"
$presentationFramework = Join-Path $gac "GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll"
$windowsFormsIntegration = Join-Path $gac "GAC_MSIL\WindowsFormsIntegration\v4.0_4.0.0.0__31bf3856ad364e35\WindowsFormsIntegration.dll"

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
    /reference:$systemXaml `
    /reference:$windowsBase `
    /reference:$presentationCore `
    /reference:$presentationFramework `
    /reference:$windowsFormsIntegration `
    /reference:System.Web.Extensions.dll `
    /reference:System.Security.dll `
    $src

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $out"
