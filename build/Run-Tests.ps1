#requires -Version 5.1
<#
.SYNOPSIS
  Builds the solution and runs every xUnit v3 test executable directly.

.DESCRIPTION
  This exists because `dotnet test` does not work in this toolchain.

  On the .NET 10 SDK, VSTest is gone - Microsoft.Testing.Platform.MSBuild fails the build
  outright with "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
  on .NET 10 SDK and later" - so MTP is mandatory and global.json opts into it. But `dotnet test`
  then reports "Zero tests ran" with exit code 5 for every assembly, including assemblies that
  demonstrably contain passing tests. Verified against xunit.v3 4.0.0 / Microsoft.Testing.Platform
  2.3.3 / SDK 10.0.400.

  Running the same assemblies directly works correctly. xUnit v3 test projects are self-executing
  console apps - that is the framework's native execution model - so this script is not a hack
  around a misconfiguration, it is the path that actually runs.

  Revisit `dotnet test` after an xunit.v3 or Microsoft.Testing.Platform bump. If it starts
  working, delete this script and put `dotnet test` back in the workflows.

.PARAMETER Configuration
  Build configuration. Defaults to Debug.

.PARAMETER NoBuild
  Skip the build and run whatever is already in bin/.

.PARAMETER IncludePerf
  Also run the performance budget gate. Off by default because it takes minutes, publishes
  nothing, and its numbers are meaningless unless measured against a published build - see
  build/publish.ps1 and REQUIREMENTS section 6.1.

.PARAMETER TimeoutSeconds
  Wall-clock limit per test assembly. A hung assembly would otherwise run until the workflow's
  own timeout killed the whole job, with no assembly name attached. This turns that into a named
  failure for the one assembly responsible. Defaults to 300.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoBuild,

    [switch] $IncludePerf,

    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$projects = @(
    'tests/Courier.Core.Tests/Courier.Core.Tests.csproj'
    'tests/Courier.Scanner.Tests/Courier.Scanner.Tests.csproj'
    'tests/Courier.Scripting.Tests/Courier.Scripting.Tests.csproj'
)

if (-not $NoBuild) {
    Write-Host 'Building' -ForegroundColor Cyan
    & dotnet build "$(Join-Path $repo 'Courier.slnx')" -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

$failed = @()

foreach ($project in $projects) {
    $full = Join-Path $repo $project
    if (-not (Test-Path $full)) { continue }

    Write-Host ''
    Write-Host "--- $project" -ForegroundColor Cyan

    # System.Diagnostics.Process rather than Start-Process, for two reasons that both bit here:
    #
    #  - Start-Process -PassThru returns a Process whose ExitCode is empty, so every suite was
    #    reported as failed while passing. This keeps the handle and reads a real exit code.
    #  - ArgumentList is populated one entry at a time, so a repository under "D:\My Work\..."
    #    is passed as one argument instead of two. Start-Process joins with spaces and quotes
    #    nothing, and dotnet then reports that "D:\My" does not exist.
    #
    # Started rather than invoked at all so a hung assembly can be named and killed - otherwise it
    # runs until the workflow's own timeout takes down the whole job with no assembly name
    # attached.
    # A quoted Arguments string rather than ArgumentList: this script declares 5.1, and Windows
    # PowerShell 5.1 runs on .NET Framework where ProcessStartInfo.ArgumentList does not exist and
    # comes back null. The quotes around the path are what a repository under "D:\My Work\..."
    # needs either way.
    $arguments = "run --project `"$full`" -c $Configuration -v q"
    if ($NoBuild) { $arguments += ' --no-build' }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = $arguments
    $startInfo.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::Start($startInfo)

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        Write-Host "TIMED OUT after ${TimeoutSeconds}s: $project" -ForegroundColor Red
        $failed += "$project (timed out)"
        continue
    }

    if ($process.ExitCode -ne 0) { $failed += "$project (exit $($process.ExitCode))" }
    $process.Dispose()
}

if ($IncludePerf) {
    Write-Host ''
    Write-Host '--- performance budgets' -ForegroundColor Cyan

    # Points the harness at a published build when one exists. Measuring the framework-dependent
    # bin/ output instead misses PERF-01 by roughly a factor of five, and reporting that as a
    # failure would be reporting on a configuration nobody ships.
    $published = Join-Path $repo 'artifacts/app/win-x64'
    if (Test-Path (Join-Path $published 'Courier.exe')) {
        $env:COURIER_PUBLISH_DIR = $published
    }
    else {
        Write-Host 'No published build found. Run build/publish.ps1 first for meaningful PERF-01 numbers.' -ForegroundColor Yellow
    }

    & dotnet run --project "$(Join-Path $repo 'tests/Courier.Perf.Tests/Courier.Perf.Tests.csproj')" -c Release -v q
    if ($LASTEXITCODE -ne 0) { $failed += 'performance budgets' }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "Failed: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host 'All test projects passed.' -ForegroundColor Green
