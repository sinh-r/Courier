# Needs live validation

Every code path that could not be exercised from the machine this was built on, and what it would
take to prove each one.

This list exists because the alternative is worse: code that compiles, is unit-tested against
fakes, and has never once talked to the thing it was written for, presented as if it were finished.
Everything here is production-shaped and unit-tested. None of it has completed a real handshake.

**Nothing on this list should be described as working until its check passes.**

---

## 1. Entra ID and the WAM broker

`Courier.Platform.Windows/Auth/WindowsPlatformAuth.cs`, `Courier.Core/Auth/EntraAuthProvider.cs`

| Requirement | What is unproven |
|---|---|
| ENT-01 | Silent SSO from the machine's existing Windows sign-in through the WAM broker. |
| ENT-02 | Client credentials, device code, and authorization code + PKCE against a real tenant. |
| ENT-05 | Silent refresh firing before expiry without an interactive prompt. |

**Why it could not be tested.** No Entra tenant, and no domain-joined machine.

**The specific risk.** TECH_SPEC §1.2 and §7.2 both flag window-handle parenting under Avalonia as
the likeliest wall in the whole plan. `ActiveWindowHandleProvider` supplies the HWND from
`TopLevel.TryGetPlatformHandle()`, which is the documented approach and looks correct — but a
broker dialog parented to the wrong window appears *behind* the app, and the user sees a hang with
no way out. That failure mode does not surface in any test that does not raise a real prompt.

**How to check.**

1. On a domain-joined machine signed in to the tenant, create an auth profile of kind
   `EntraWindowsSignIn` with the tenant, client id and a scope.
2. Send a request. It must succeed **with no prompt at all** — that is ENT-01, and a prompt means
   the broker is not being used.
3. Open a settings dialog and trigger an interactive sign-in from there. The broker dialog must
   appear in front of *that dialog*, not behind the main window.
4. Wait for the token to approach expiry with `RefreshLeewaySeconds` set low. The next send must
   refresh silently.

## 2. Developer tool token reuse

`Courier.Core/Auth/DeveloperToolTokenSource.cs` — ENT-03

**Unproven.** That `AzureCliCredential`, `VisualStudioCredential` and `AzurePowerShellCredential`
return usable tokens, and that `CredentialUnavailableException` is what a machine without the tool
actually throws.

**Check.** Run `az login`, then send a request with `ReuseDeveloperToolTokens` on. It must succeed
without prompting, and the auth panel must say the token came from the Azure CLI. Then test on a
machine with no Azure CLI: it must fall through quietly to the next path, with no dialog.

## 3. NTLM and Kerberos

`Courier.Platform.Windows/Auth/WindowsIntegratedAuth.cs` — ENT-07

**Unproven.** That `HttpClientHandler` with `UseDefaultCredentials` completes a challenge against a
real Windows-authenticated endpoint, with no credential typed.

**Check.** On a domain-joined machine, call an internal endpoint configured for Windows
authentication. Confirm a 401 challenge is answered automatically, and that the status bar shows
the identity being presented.

## 4. Client certificates, especially smart cards

`Courier.Platform.Windows/Certificates/WindowsCertificateSource.cs` — ENT-08

**Partially proven.** Enumeration from `X509Store` works on this machine, which has no
hardware-backed certificates.

**Unproven.** That a smart-card certificate signs through CNG without the private key being
touched, and that `IsHardwareBacked` detects real card providers rather than only the strings it
looks for.

**The specific risk.** Anything that tries to export the private key throws on a smart card.
Nothing in this file does — that is deliberate and commented — but the TLS stack's own path with a
card present has not been exercised.

**Check.** With a smart card inserted, select its certificate for a host requiring mutual TLS. The
handshake must complete, a PIN prompt (if any) must appear in front of the app, and removing the
card mid-session must produce the plain-language error rather than an exception.

## 5. Corporate proxy and PAC

`Courier.Platform.Windows/Proxy/WinHttpProxyResolver.cs` — ENT-10

**Unproven.** WinHTTP PAC evaluation and proxy authentication against a real corporate proxy.

**The specific risk.** This deviates from TECH_SPEC §3.1 deliberately: PAC is evaluated here and
the answer is handed to `SocketsHttpHandler`, rather than using `WinHttpHandler` for the whole
request, because WinHttpHandler cannot do HTTP/3 and CORE-01 requires it. That trade is sound in
principle and unverified in practice — in particular, proxy *authentication* is handled by
SocketsHttpHandler rather than by WinHTTP, and a proxy that challenges with Negotiate may behave
differently.

**Check.** On a machine behind a PAC-configured, authenticating proxy: confirm the trust and
network page shows the PAC URL and the resolved proxy with the right source label; confirm a
request through it succeeds; confirm a bypassed host goes direct.

## 6. TLS inspection and chain failures

`Courier.Platform.Windows/Certificates/WindowsTrustStore.cs` — ENT-09, ENT-11

**Partially proven.** Chain building and per-element status extraction work against ordinary
public certificates.

**Unproven.** The behaviour behind a TLS-inspecting proxy whose root CA IT installed — the case
ENT-09 exists for — and whether the explanations name the right certificate when a re-signed chain
fails.

**Check.** Behind a TLS-inspecting proxy: with the corporate root in the machine store, requests
must succeed with no configuration. Then remove the root and confirm the error names *which*
certificate failed and why, and that the only remedy offered is a per-host exception. Confirm there
is no global "disable verification" anywhere in the UI. Re-issue the certificate and confirm the
recorded exception stops applying.

## 7. Application Insights

`Courier.Telemetry/AppInsights/AppInsightsSource.cs` — TEL-02, TEL-04, TEL-05

**Unproven.** The KQL queries against a real workspace.

**The specific risk.** The queries are written against the standard schema, but the union in
`GetTimelineAsync` projects columns that only exist on some of the unioned tables. `Text()` and
`Number()` swallow `ArgumentException` for exactly that reason, which means a schema mismatch
degrades to empty fields rather than an error — quiet, and hard to notice.

Also unproven: whether the request body is where `SearchAsync` looks for it
(`customDimensions['RequestBody']`). That is a convention, not a standard. Where an application
logs it elsewhere, every result will report `RouteAndHeaders` fidelity, which is honest but useless.

**Check.** Against a real workspace: search by an operation id from an alert and confirm the
timeline shows dependencies and exceptions; confirm a request whose body *was* logged reports full
fidelity and reconstructs with that body; confirm one that was not reports partial fidelity and
produces a placeholder rather than an empty payload.

## 8. Datadog

`Courier.Telemetry/Datadog/DatadogSource.cs` — TEL-03

**Unproven.** The v2 spans search request and response shape, and the custom-attribute paths
(`custom.http.url`, `custom.error.type`).

**Check.** With an API key and application key in the credential store: search by trace id and by
URL; confirm results and fidelity are right; confirm the keys never appear in any file Courier
writes.

## 9. Azure DevOps attachment

`Courier.Telemetry/AzureDevOps/WorkItemAttachmentClient.cs` — CAP-08

**Unproven.** Upload, the JSON-patch link step, and download, against a real organisation.

**Check.** Attach a capsule to a work item and confirm it appears on the item. Then open it from
the attachment on a different machine and confirm it imports. Confirm a token lacking work-item
scope produces the sentence naming the missing permission rather than a bare 403.

## 10. HTTP/3

`Courier.Core/Privacy/EgressGate.cs` — CORE-01

**Unproven.** No HTTP/3 endpoint was available. The handler requests `HttpVersion.Version30` with
`RequestVersionExact` when a request specifies it, which requires msquic on the machine.

**Check.** Call a known HTTP/3 endpoint with the version pinned to 3.0 and confirm the response
reports HTTP/3. Confirm that on a machine without msquic the failure is a clear message rather than
a silent downgrade.

## 11. Semantic scanner tier

`Courier.Scanner/Semantic/` — SCAN-04, SCAN-08 (upgrade path)

**Unproven.** `MSBuildWorkspace.OpenSolutionAsync` against a real solution, and therefore DTO
graph walking for sample bodies.

**Status.** `DtoSampleGenerator` is written and works against `ITypeSymbol`, but the workspace
loading that supplies those symbols has not been run. The syntax tier is fully working and tested,
and correctly reports "load the solution to generate a sample body" where it cannot resolve a type
— so the degradation is visible rather than silent.

**Check.** Open a real solution that restores. Confirm sample bodies appear for `[FromBody]`
parameters, that they honour `[Required]`, `[Range]`, `[StringLength]` and enums, and that a
self-referencing DTO terminates.

## 12. Performance: one budget is not met

`tests/Courier.Perf.Tests` — PERF-01 through PERF-07

Measured on a 16-core / 15.7GB machine with an SSD, against a published single-file ReadyToRun
build. REQUIREMENTS §6.1 specifies a 4-core, 16GB, **HDD-backed** corporate image, so every number
below is from more generous hardware than the one that counts.

| Budget | Measured | Limit | | |
|---|---|---|---|---|
| PERF-01 cold start | 875 ms | 1500 ms | pass | 58% of budget |
| **PERF-01 warm start** | **852 ms** | **800 ms** | **FAIL** | **106% of budget** |
| PERF-02 100 tabs | 646 MB | 900 MB | pass | 72% |
| PERF-03 tab switch | 0.77 ms | 50 ms | pass | 2% |
| PERF-05 index 50MB | 489 ms | 2000 ms | pass | 24% |
| PERF-05 search 50MB | 41 ms | 64 ms | pass | 64% |
| PERF-05 expand node | 5.7 ms | 16 ms | pass | 36% |
| PERF-06 rescan 200 | 12 ms | 3000 ms | pass | 1% |
| PERF-07 idle CPU | 0.06% | 1% | pass | 6% |

### PERF-01 warm start does not meet its budget

**852 ms against 800 ms.** Reproducible: five consecutive launches gave 827–853 ms with the median
at 852. This is not measurement noise, and it is on hardware faster than the reference machine, so
it will be worse there rather than better.

**What has already been done.** ReadyToRun with a single-file self-contained publish (TECH_SPEC §4);
compression off in the bundle, which trades ~100 ms of startup for disk size; everything
non-essential deferred past first paint by `StartupJobQueue` — no scanner, no telemetry, no git
status, no database open; and MSAL and Azure.Identity moved behind `Lazy<T>` so their assembly
graphs no longer load on a launch that never authenticates.

**What has not been tried, in order of likely effect.**

1. **Trimming.** The published executable is 326 MB, self-contained and untrimmed. Mapping it is a
   real part of the remaining time. Trimming an Avalonia app risks reflection breakage and needs a
   full UI pass to validate, which is why it was not done blind.
2. **NativeAOT.** TECH_SPEC §1.2 and §4 both say to consider it for the app *only* with constrained
   XAML, and to measure before committing. That constraint is a product decision, not a build flag.
3. **Framework-dependent deployment** alongside the portable build, for machines that already have
   the runtime. Smaller to map, but it gives up the "no installer, no admin rights" property that
   NFR-02 exists for, so it would be an additional artifact rather than a replacement.

**Do not treat this as met.** REQUIREMENTS §6.1 calls the budgets release gates, and the CI job runs
this harness on every build, so the failure is visible rather than filed away.

### PERF-04 is not measured at all

Keystroke-to-render under 16 ms at a 1 MB document. The design meets it — validation and
highlighting are debounced 250 ms off the UI thread and the keystroke path does no parsing — but
proving it needs a UI-level harness driving real input through AvaloniaEdit, not the headless
measurement used for everything else. **Unverified is not the same as met.**

### Re-measure first on the reference machine

PERF-01 (both) and PERF-02 have the least headroom and are the most hardware-sensitive: cold start
is dominated by disk on an HDD-backed image, and working set does not shrink on a smaller machine.

**Check.**

```powershell
./build/publish.ps1 -Runtime win-x64
$env:COURIER_PUBLISH_DIR = "$PWD/artifacts/app/win-x64"
dotnet run --project tests/Courier.Perf.Tests -c Release
```

---

## Summary

| Area | Requirements | State |
|---|---|---|
| Entra / WAM | ENT-01, 02, 05 | Wired, untested against a tenant |
| Developer tool tokens | ENT-03 | Wired, untested |
| NTLM / Kerberos | ENT-07 | Wired, untested |
| Smart cards | ENT-08 | Store enumeration proven; card signing untested |
| PAC proxy | ENT-10 | Wired, untested behind a real proxy |
| TLS inspection | ENT-09, 11 | Chain logic proven; inspecting proxy untested |
| App Insights | TEL-02, 04, 05 | Wired, untested |
| Datadog | TEL-03 | Wired, untested |
| Azure DevOps | CAP-08 | Wired, untested |
| HTTP/3 | CORE-01 | Wired, untested |
| Semantic scan | SCAN-04 | Generator written; workspace loading untested |
| Performance | PERF-01…07 | 8 of 9 pass on faster hardware. PERF-01 warm start MISSES at 852ms vs 800ms. PERF-04 unmeasured. |
