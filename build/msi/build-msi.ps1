#requires -Version 5.1
<#
.SYNOPSIS
    Builds the MSI from a published tree. NFR-03.

.DESCRIPTION
    Uses WiX 7 as a local dotnet tool so the build needs no machine-wide install.
    Run build/publish.ps1 first; this packages what that produced.
#>
[CmdletBinding()]
param(
    [string] $PublishDir,
    [string] $Version = '0.1.0',
    [ValidateSet('x64', 'arm64')]
    [string] $Platform = 'x64',
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $PublishDir) {
    $rid = if ($Platform -eq 'arm64') { 'win-arm64' } else { 'win-x64' }
    $PublishDir = Join-Path $repo "artifacts/app/$rid"
}

if (-not (Test-Path (Join-Path $PublishDir 'Courier.exe'))) {
    throw "No published build in $PublishDir. Run build/publish.ps1 first."
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repo "artifacts/Courier-$Version-$Platform.msi"
}

# WiX as a local tool: a machine-wide install is one more thing for a contributor to get wrong.
if (-not (Test-Path (Join-Path $repo '.config/dotnet-tools.json'))) {
    & dotnet new tool-manifest --output $repo | Out-Null
}

& dotnet tool restore --tool-manifest (Join-Path $repo '.config/dotnet-tools.json') 2>$null
if ($LASTEXITCODE -ne 0) {
    & dotnet tool install wix --version 7.0.0 --tool-manifest (Join-Path $repo '.config/dotnet-tools.json')
}

# Harvest the published tree so the component list never drifts from what was published.
$harvested = Join-Path $env:TEMP "courier-files-$([guid]::NewGuid().ToString('n')).wxs"

& dotnet wix harvest files $PublishDir `
    --directory-id INSTALLFOLDER `
    --component-group-id PublishedFiles `
    --out $harvested

if ($LASTEXITCODE -ne 0) { throw 'WiX harvest failed' }

try {
    & dotnet wix build `
        (Join-Path $PSScriptRoot 'Courier.wxs') $harvested `
        -d "Version=$Version" `
        -d "PublishDir=$PublishDir" `
        -arch $Platform `
        -ext WixToolset.UI.wixext `
        -out $OutputPath

    if ($LASTEXITCODE -ne 0) { throw 'WiX build failed' }
}
finally {
    Remove-Item $harvested -Force -ErrorAction SilentlyContinue
}

Write-Host "MSI: $OutputPath" -ForegroundColor Green
Write-Host 'Remember to sign it. An unsigned MSI will not survive corporate application control.' -ForegroundColor Yellow
