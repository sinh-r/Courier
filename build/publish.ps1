#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Courier in the configuration that ships.

.DESCRIPTION
    Single-file, self-contained, ReadyToRun, per RID. TECH_SPEC section 5.

    The configuration matters: a framework-dependent build misses PERF-01's 1.5s cold start by
    roughly a factor of five. Publishing this way is not an optimisation, it is the product.

    Authenticode signing is wired but unsigned unless a certificate is supplied. An unsigned binary
    will not survive corporate application control, so this is the one step that must be completed
    before a real release.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64'),

    [string] $Configuration = 'Release',

    [string] $OutputRoot,

    # Path to a .pfx, or a thumbprint already in the certificate store.
    [string] $SigningCertificate,

    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    [switch] $SkipCli
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts' }

function Invoke-Sign {
    param([string] $Path)

    if (-not $SigningCertificate) {
        Write-Host "  unsigned: $(Split-Path -Leaf $Path)" -ForegroundColor DarkYellow
        return
    }

    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        throw 'signtool.exe is not on PATH. Install the Windows SDK, or omit -SigningCertificate.'
    }

    $args = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl)
    if (Test-Path $SigningCertificate) { $args += @('/f', $SigningCertificate) }
    else { $args += @('/sha1', $SigningCertificate) }

    & $signtool.Source @args $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path" }
}

foreach ($rid in $Runtime) {
    Write-Host ''
    Write-Host "Publishing $rid" -ForegroundColor Cyan

    $appOut = Join-Path $OutputRoot "app/$rid"

    # EnableCompressionInSingleFile is deliberately off: it trades roughly 100ms of cold start for
    # disk size, and PERF-01 is a release gate while disk size is not.
    & dotnet publish (Join-Path $repo 'src/Courier.App/Courier.App.csproj') `
        --framework net10.0-windows10.0.19041.0 `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugType=none `
        -p:ContinuousIntegrationBuild=true `
        --output $appOut `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # The published tree carries pdbs and MSBuild host folders that the portable zip does not need.
    Get-ChildItem $appOut -Include '*.pdb' -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
    foreach ($unwanted in 'BuildHost-net472', 'BuildHost-netcore') {
        $path = Join-Path $appOut $unwanted
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }

    Invoke-Sign (Join-Path $appOut 'Courier.exe')

    # NFR-02: a portable zip needing no installer and no admin rights. This is how Courier gets
    # into a locked-down environment before IT approves anything.
    $zip = Join-Path $OutputRoot "Courier-$rid-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $appOut '*') -DestinationPath $zip
    Write-Host "  portable: $zip" -ForegroundColor Green
}

if (-not $SkipCli) {
    Write-Host ''
    Write-Host 'Packing the CLI and libraries' -ForegroundColor Cyan

    # Deliverables 2 and 3: the dotnet tool, and the core library on NuGet so the scanner and
    # runner are reusable.
    & dotnet pack (Join-Path $repo 'Courier.slnx') `
        --configuration $Configuration `
        -p:ContinuousIntegrationBuild=true `
        --output (Join-Path $OutputRoot 'nuget') `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw 'Pack failed' }
}

Write-Host ''
Write-Host "Artifacts in $OutputRoot" -ForegroundColor Green

if (-not $SigningCertificate) {
    Write-Host ''
    Write-Host 'NOT SIGNED. Authenticode signing is required before release: an unsigned binary' -ForegroundColor Yellow
    Write-Host 'will not survive corporate application control. Re-run with -SigningCertificate.' -ForegroundColor Yellow
}
