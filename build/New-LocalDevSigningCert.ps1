<#
.SYNOPSIS
  One-time setup: creates and trusts the local dev code-signing certificate that
  Sign-LocalDevBinary.ps1 uses to work around Windows Smart App Control.

.DESCRIPTION
  Run this once, by hand, if `dotnet build` on Courier.App fails with something like:

      CS8034: Unable to load Analyzer assembly ...Avalonia.Analyzers.CSharp.dll ...
      An Application Control policy has blocked this file. (0x800711C7)

  or a freshly-built test binary fails to run with:

      System.IO.FileLoadException: ... An Application Control policy has blocked this
      file. (0x800711C7)

  Both are Windows Smart App Control (Code Integrity) refusing to load an unsigned binary
  it has not built reputation for  -  not a virus detection, and not specific to this repo.
  `Get-WinEvent -LogName "Microsoft-Windows-CodeIntegrity/Operational"` shows a matching
  "Smart App Control Block Details" event when this is the cause.

  This script is NOT part of the build itself  -  Directory.Build.targets' signing steps
  silently no-op when the certificate below is absent, so a contributor who never hits this
  problem, and CI, never need to run it.

  Creates a self-signed code-signing certificate in Cert:\CurrentUser\My, then trusts it via
  Cert:\CurrentUser\TrustedPublisher  -  deliberately not Cert:\CurrentUser\Root. Windows Code
  Integrity honors TrustedPublisher directly and does not require a full chain to a trusted
  root, so nothing here asks Windows to trust this certificate as a root of anything else.
  This is local dev tooling only, never a substitute for the real release signing the
  release pipeline does with a CA-issued certificate.

.PARAMETER Subject
  Overrides the certificate's subject name. Defaults to what Sign-LocalDevBinary.ps1 looks for.
#>
param(
    [string]$Subject = "CN=Courier Local Dev Signing"
)

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject } |
    Select-Object -First 1

if ($existing) {
    Write-Host "New-LocalDevSigningCert: '$Subject' already exists (thumbprint $($existing.Thumbprint)), nothing to do."
    return
}

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -NotAfter (Get-Date).AddYears(5)

$exportPath = Join-Path ([System.IO.Path]::GetTempPath()) "courier-local-dev-signing.cer"

try {
    Export-Certificate -Cert $cert -FilePath $exportPath | Out-Null
    Import-Certificate -FilePath $exportPath -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
}
finally {
    Remove-Item $exportPath -ErrorAction SilentlyContinue
}

Write-Host "New-LocalDevSigningCert: created and trusted '$Subject' (thumbprint $($cert.Thumbprint))."
Write-Host "Rebuild Courier.App  -  Directory.Build.targets will sign the blocked files automatically from now on."
