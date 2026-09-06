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
#
# The manifest is written here rather than by `dotnet new tool-manifest`. That command writes to
# <dir>/dotnet-tools.json rather than <dir>/.config/dotnet-tools.json, so `dotnet tool restore`
# cannot find what it just created - and on the second run it refuses with exit 73 because the
# stray file is in its way. The file is one line of stable schema; writing it here removes a
# dependency on the template engine for no loss.
$manifest = Join-Path $repo '.config/dotnet-tools.json'
if (-not (Test-Path $manifest)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifest) | Out-Null
    '{ "version": 1, "isRoot": true, "tools": {} }' | Set-Content -Path $manifest -Encoding UTF8
}

# Probing whether a tool is present means running a command that is expected to fail, and in
# Windows PowerShell 5.1 that is not as simple as checking $LASTEXITCODE: with
# $ErrorActionPreference = 'Stop', *any* output a native executable writes to stderr is turned into
# a terminating NativeCommandError, and 2>$null does not prevent it because the redirection happens
# after PowerShell has already routed it through the error stream. The probes below therefore drop
# to 'Continue' and read the exit code, which is the only reliable signal here.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    # Fast path: the manifest already lists wix, so this installs it from the lock file.
    & dotnet tool restore --tool-manifest $manifest *> $null

    # Not keyed off restore's exit code: `dotnet tool restore` exits 0 on a manifest that lists no
    # tools, so a fresh checkout would report success and never install anything - which is how the
    # MSI came to be missing from a release whose MSI step reported success.
    & dotnet wix --version *> $null
    $wixIsUsable = ($LASTEXITCODE -eq 0)

    if (-not $wixIsUsable) {
        & dotnet tool install wix --version 5.0.2 --tool-manifest $manifest *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Could not install the WiX tool.' }
    }
}
finally {
    $ErrorActionPreference = $previousPreference
}

# There is no harvest step, and there was never a working one.
#
# The original script called `wix harvest files`, which does not exist: WiX 7's command set is
# build, eula, msi, burn, extension, convert and format. It failed on every release, inside a step
# that swallows failures, so the MSI was simply absent from a release whose MSI step reported
# success.
#
# Harvesting was also solving a problem this build does not have. build/publish.ps1 produces a
# self-extracting single file, so the payload is exactly one exe. It is authored directly in
# Courier.wxs, where a reviewer can see what the installer installs.

# WiX 5, deliberately, and not the newer 7.
#
# WiX 7 refuses to run at all until its Open Source Maintenance Fee EULA is accepted (WIX7015),
# which a build script can do with `wix eula accept wix7`. That is a licensing commitment about how
# an organization funds the toolset, not a build setting, so it is not something this script should
# make on a user's behalf inside CI. WiX 5 has no such gate, identical authoring for what
# Courier.wxs uses, and is Microsoft-independent and maintained.
#
# If the project later decides to accept the OSMF terms, bump the two version numbers below and add
# `wix eula accept wix7` before the first wix invocation. Nothing else here changes.

# The UI extension has to be in the local cache before -ext can resolve it, and `extension add` is
# idempotent.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { & dotnet wix extension add WixToolset.UI.wixext/5.0.2 *> $null }
finally { $ErrorActionPreference = $previousPreference }

& dotnet wix build `
    (Join-Path $PSScriptRoot 'Courier.wxs') `
    -d "Version=$Version" `
    -d "PublishDir=$PublishDir" `
    -arch $Platform `
    -ext WixToolset.UI.wixext `
    -out $OutputPath

if ($LASTEXITCODE -ne 0) { throw 'WiX build failed' }
if (-not (Test-Path $OutputPath)) { throw "WiX reported success but produced no file at $OutputPath" }

Write-Host ("MSI: {0} ({1:N0} MB)" -f $OutputPath, ((Get-Item $OutputPath).Length / 1MB)) -ForegroundColor Green
Write-Host 'Remember to sign it. An unsigned MSI will not survive corporate application control.' -ForegroundColor Yellow
