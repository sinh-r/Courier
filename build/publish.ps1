#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Courier in the configuration that ships.

.DESCRIPTION
    Single-file, self-contained, ReadyToRun, per RID. TECH_SPEC section 5.

    The configuration matters and is not an optimisation: PERF-01 gives 1.5s to a cold start, and a
    framework-dependent build misses it by roughly a factor of five. This is the product.

    One publish per RID, producing one executable. An earlier version published twice - once as a
    folder with the native libraries beside the exe, once as a self-extracting single file - and
    the second publish failed on CI, because two publishes of the same project with different
    single-file settings share obj/ and the incremental state does not survive the difference.
    There was never a reason for both: a single exe is what "portable, no installer, no admin
    rights" (NFR-02) actually means, and the zip now just carries that exe with its licence.

    Authenticode signing is wired but inert without a certificate. TECH_SPEC section 5 is blunt
    that an unsigned binary will not survive corporate application control, which is exactly the
    environment Courier targets, so this is the one step that must be completed before a real
    release.
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

    $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl)
    if (Test-Path $SigningCertificate) { $arguments += @('/f', $SigningCertificate) }
    else { $arguments += @('/sha1', $SigningCertificate) }

    & $signtool.Source @arguments $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path" }
}

foreach ($rid in $Runtime) {
    Write-Host ''
    Write-Host "Publishing $rid" -ForegroundColor Cyan

    $appOut = Join-Path $OutputRoot "app/$rid"

    # IncludeNativeLibrariesForSelfExtract is load-bearing: Skia, HarfBuzz, e_sqlite3 and the MSAL
    # broker runtime are all native, and without it a lone Courier.exe fails at runtime rather than
    # at build time.
    #
    # EnableCompressionInSingleFile stays off: it trades roughly 100ms of cold start for disk size,
    # and cold start is the release gate while disk size is not.
    #
    # Trimming and AOT stay off. Avalonia is reflection-heavy and neither has been validated
    # against the full UI - see docs/NEEDS_LIVE_VALIDATION.md section 12, where trimming is the
    # untried lever on the one budget currently unmet.
    & dotnet publish (Join-Path $repo 'src/Courier.App/Courier.App.csproj') `
        --framework net10.0-windows10.0.19041.0 `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugType=none `
        -p:ContinuousIntegrationBuild=true `
        --output $appOut `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # Symbols and the MSBuild host folders come along even with DebugType=none, and neither belongs
    # in something a user downloads.
    Get-ChildItem $appOut -Include '*.pdb' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
    foreach ($unwanted in 'BuildHost-net472', 'BuildHost-netcore') {
        $path = Join-Path $appOut $unwanted
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }

    $exe = Join-Path $appOut 'Courier.exe'
    if (-not (Test-Path $exe)) {
        Get-ChildItem $appOut
        throw "Courier.exe not found in $appOut - check <AssemblyName> in Courier.App.csproj"
    }

    Invoke-Sign $exe

    Write-Host ("  single file: {0:N0} MB" -f ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green

    # NFR-02: a portable download needing no installer and no admin rights. This is how Courier
    # gets into a locked-down environment before IT approves anything. The licence travels with it
    # because Apache 2.0 requires it to.
    $staging = Join-Path $OutputRoot "staging-$rid"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging | Out-Null

    Copy-Item $exe $staging
    foreach ($extra in 'LICENSE', 'README.md') {
        $source = Join-Path $repo $extra
        if (Test-Path $source) { Copy-Item $source $staging }
    }

    $zip = Join-Path $OutputRoot "Courier-$rid-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item $staging -Recurse -Force

    Write-Host "  portable: $zip" -ForegroundColor Green

    # Hands the exact paths to the workflow rather than making it reconstruct them. A release run
    # already failed once on a path this script had actually written correctly, and every step
    # downstream - upload, sign, attest, hash, release - needs the same two paths. One source for
    # them, emitted by the thing that created the files.
    if ($env:GITHUB_OUTPUT) {
        "exe=$($exe -replace '\\', '/')" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
        "zip=$($zip -replace '\\', '/')" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
    }
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
