<#
.SYNOPSIS
  Signs every unsigned DLL/EXE in a directory with the Courier local dev signing certificate.

.DESCRIPTION
  Adapted from EventPublisherConsumer's Sign-LocalTestBinary.ps1, which found and fixed the
  same Windows Smart App Control problem. Two differences here: this signs a project's own
  build output regardless of whether it is a test project (Courier.App's own output has been
  blocked at run time too, not only test binaries), and it is also called before compilation
  to sign the NuGet-cached Avalonia analyzer DLLs that fail to load with CS8034  -  see
  Directory.Build.targets for both call sites.

  Windows Smart App Control blocks freshly-built or freshly-restored, unsigned binaries on
  some machines  -  not only a project's own output assembly, but its dependencies too
  (Avalonia.*.dll, SQLitePCLRaw.provider.e_sqlite3.dll and the Avalonia analyzer/generator
  DLLs under the NuGet package cache have all been observed blocked independently of each
  other). Signing every DLL/EXE under the given directory, not just one file, is what
  actually makes this reliable.

  Signs with a self-signed certificate installed into CurrentUser\TrustedPublisher, which is
  enough for local execution. This is NOT a substitute for real release signing (a CA-issued
  certificate, or a free OSS signing service)  -  that is a separate concern for the release
  pipeline, not local development.

  Silently no-ops if the certificate isn't present (see New-LocalDevSigningCert.ps1), so this
  is safe to wire into every contributor's build even if they have not run that setup step.
  Also silently skips files that are already signed (whether by this cert or another), so
  re-running after an incremental build only touches what actually needs it.

  Expected status: Set-AuthenticodeSignature reports "UnknownError" / "terminated in a root
  certificate which is not trusted" for this signature, because the certificate is
  self-signed and only installed into CurrentUser\TrustedPublisher, not CurrentUser\Root.
  That's fine  -  Windows Code Integrity (what Smart App Control enforces on) honors
  TrustedPublisher directly and does not require a full chain to a trusted root, which is the
  whole point of using that store here rather than Root. This script only warns if signing
  itself failed outright (e.g. the private key wasn't marked exportable, or the file is in
  use), not on that expected chain-status mismatch.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory,

    [string]$Subject = "CN=Courier Local Dev Signing"
)

if (-not (Test-Path $Directory)) {
    return
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject } |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "Sign-LocalDevBinary: no local dev signing cert found, skipping signature for $Directory"
    return
}

$files = Get-ChildItem $Directory -Include "*.dll", "*.exe" -Recurse -ErrorAction SilentlyContinue
$expectedStatuses = "Valid", "UnknownError"

foreach ($file in $files) {
    $existing = Get-AuthenticodeSignature -FilePath $file.FullName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne "NotSigned") {
        continue
    }

    $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -ErrorAction SilentlyContinue
    if ($null -eq $result -or $result.Status -notin $expectedStatuses) {
        Write-Warning "Sign-LocalDevBinary: signing failed for $($file.FullName) (status: $($result.Status))"
    }
}
