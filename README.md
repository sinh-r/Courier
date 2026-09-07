# Courier

A local-first API client for Windows enterprise .NET teams.

No account. No sync. No telemetry. Collections are plain YAML files on disk that git already
versions, and credentials stay in Windows Credential Manager.

---

## What makes it different

**Nothing leaves the machine.** There is no account system and no server this project operates, so
there is nowhere for your collections, tokens or endpoint inventories to be uploaded to. Every
outbound socket in the process goes through one auditable chokepoint, and the build fails if any
other code opens one.

**Enterprise auth that works.** Entra ID with broker SSO, mutual TLS from the Windows certificate
store, corporate proxies with PAC and TLS inspection, and NTLM/Kerberos with the current Windows
identity. This is the part no other client does.

**The collection builds itself from your code.** Point Courier at a .NET solution and it derives
every endpoint from the controllers, then keeps them current on every build — without destroying
anything you customised.

**A failing request is one file.** Export a *capsule* — the request, the response, the trace id,
secrets stripped — and hand it to whoever can debug it.

## Getting started

```powershell
git clone https://github.com/courier-api/courier
cd courier

dotnet build Courier.slnx
dotnet run --project src/Courier.App -f net10.0-windows10.0.19041.0
```

For the configuration that ships — single-file, self-contained, ReadyToRun:

```powershell
./build/publish.ps1 -Runtime win-x64
```

## The CLI

```powershell
dotnet tool install -g Courier.Cli

courier scan ./src/Orders.Api --output ./collections/orders
courier run ./collections/orders --environment QA-Internal --junit results.xml
courier export ./collections/orders --format curl --request orders
courier capsule inspect ./orders-500.capsule --redactions
```

`courier run` exits non-zero on a failing assertion and writes JSON or JUnit XML, so it drops into
CI as-is.

## Build-time collection generation

```xml
<PackageReference Include="Courier.MSBuild" Version="0.1.0" PrivateAssets="all" />
<PropertyGroup>
  <CourierCollectionPath>$(MSBuildProjectDirectory)\..\collections\orders</CourierCollectionPath>
</PropertyGroup>
```

The collection updates on every build without the app open. The generator only ever writes
`endpoints.generated.yaml`; your edits live in `endpoints.overlay.yaml` and are never touched.

## Repository layout

```
src/
  Courier.Core/              collection model, HTTP, variables, auth, capsules, egress gate
  Courier.Scanner/           Roslyn source analysis
  Courier.Scripting/         Jint sandbox, pm.* shim, assertions, collection runner
  Courier.Telemetry/         App Insights, Datadog, Azure DevOps
  Courier.Platform.Windows/  credential store, certificate store, WinHTTP proxy, WAM broker
  Courier.Platform.Posix/    best-effort fallbacks
  Courier.Cli/               dotnet tool
  Courier.MSBuild/           build-task package
  Courier.App/               Avalonia
tests/                       unit tests and the performance budget gate
samples/                     reference API solutions the scanner is tested against
docs/                        specifications and the security statement
build/                       publish, MSI, winget
```

## Tests

```powershell
./build/Run-Tests.ps1
./build/Run-Tests.ps1 -IncludePerf
```

`xunit.v3` targets Microsoft.Testing.Platform, where a test project is an executable. Use
`dotnet run --project <test project>` rather than `dotnet test`.

The performance budgets in [REQUIREMENTS §6.1](docs/REQUIREMENTS.md) are release gates. CI reports
them on every build without failing on them, for two reasons set out in `.github/workflows/ci.yml`:
the budgets are specified against a 4-core HDD-backed corporate image and a GitHub runner is
different hardware in both directions, and PERF-01 warm start is currently unmet and tracked in
[NEEDS_LIVE_VALIDATION.md](docs/NEEDS_LIVE_VALIDATION.md) rather than blocking every release
behind a known gap.

## Releases

Tagging `v*` builds the Windows binaries, attests their provenance, publishes SHA256 sums and
attaches everything to a GitHub Release. Release artifacts are built by CI and only by CI — never
build one locally and upload it by hand, because that breaks the repo-to-binary chain the
attestation exists to prove.

```powershell
# Verify a download
Get-FileHash Courier.exe -Algorithm SHA256      # compare against Courier.exe.sha256
gh attestation verify Courier.exe --repo sinh-r/Courier
```

Binaries are **not yet Authenticode signed**. The signing step is wired and inert: it activates
the moment a `SIGNPATH_API_TOKEN` secret exists, with no workflow edit. Until then, corporate
application control will likely block them — which, for a tool aimed at exactly those machines, is
the most important thing left to fix in the pipeline.

## Documentation

| | |
|---|---|
| [Collection format](docs/COLLECTION_FORMAT.md) | The on-disk format, versioned so the files outlive the tool |
| [Capsule format](docs/CAPSULE_FORMAT.md) | The container and the redaction guarantee |
| [Security and privacy](docs/SECURITY_AND_PRIVACY.md) | Data locations, network behaviour, threat model, review checklist |
| [Architecture](docs/ARCHITECTURE.md) | For contributors: the four designs that cannot be retrofitted |
| [Needs live validation](docs/NEEDS_LIVE_VALIDATION.md) | Every path not yet exercised against the real thing |
| [Requirements](docs/REQUIREMENTS.md) · [Technical spec](docs/TECH_SPEC.md) | The originals |

## Status

Not released. Every library requirement has an implementation and the performance budgets pass, but
until recently the desktop app itself was a disconnected shell around them: the tab strip, the
collections rail, the theme, and the Send button all rendered but had nothing behind them.

That has been fixed for the core interactions — sending a request, switching and closing tabs,
opening a collection folder, editing params and headers, saving a request to disk, recording
history, switching light/dark theme, and closing every dialog — which is what makes the "uninstall
Postman" claim in Phase 1 of [REQUIREMENTS.md](docs/REQUIREMENTS.md) true rather than aspirational.

**Import from code** (SCAN-01..12) is now wired the same way: the menu, the command palette and the
first-run screen all reach a real scan, the sync-from-code dialog can actually start one and shows a
live diff, and Apply writes `endpoints.generated.yaml` without ever touching the overlay holding your
edits. The scanner itself was extended to read minimal APIs (`app.MapGet`, route groups held in
variables, method-group handlers, `MapMethods`) as well as attribute-routed controllers — previously
a minimal-API project scanned as zero endpoints, silently. Verified end to end against
[gothinkster/aspnetcore-realworld-example-app](https://github.com/gothinkster/aspnetcore-realworld-example-app)
(19 controller-based endpoints, 0 unresolved) and against `dotnet/eShop`'s minimal-API catalog
service, plus a new `samples/Minimal.Api` fixture with its own golden tests.

**A scanned request can now actually be sent.** Send used to build an empty `VariableScopes` — so
`{{baseUrl}}` could never resolve — then abandon the request silently the moment the URL failed to
parse; `courier run` had the only correct implementation, in `CollectionRunner`. That preparation
logic is now `RequestPreparer`, shared by both callers: it substitutes variables and path
parameters, layers default headers under the collection's under the request's own, and reports a
plain-English reason (which variable, which unfilled `{param}`, which malformed URL) instead of
doing nothing. Environments are no longer a dead end either — `EnvironmentReader`'s derived
`baseUrl` is now written to `environments/*.env.yaml` on import, `collection.yaml` is read for the
first time by the app (`Variables`, `Headers`, `Settings`, `InjectTraceParent`, all previously
inert), and the title-bar picker actually switches between them. Courier also sends an `Accept` and
a `User-Agent` for the first time, shown as overridable inherited rows in the Headers grid rather
than applied invisibly. A new Path tab holds route-parameter values, appearing only when the URL
has any. Verified end to end against a live, running clone of
[gothinkster/aspnetcore-realworld-example-app](https://github.com/gothinkster/aspnetcore-realworld-example-app):
real 200s with real bodies, an unfilled path parameter refused by name before any network call, and
a filled one reaching the server with the value substituted into the URL.

Still open, tracked as backlog rather than fixed in this pass:

- Auth is not applied on the GUI send path — `TabState.AuthProfile` is read by nothing, and no
  `IAuthProvider` is called anywhere in the app. An anonymous endpoint sends fine; an authenticated
  one will not.
- The environment **editor** (add/edit variables, the shared-vs-local split, secret-shaped-value
  warnings) does not exist — environments can be loaded and selected, not created or edited, in the
  GUI.
- Scanner-generated assertions are not evaluated on Send; the response "Tests" tab stays empty.

- A further ~24 buttons across the capsule, telemetry and first-run dialogs remain unwired (each
  maps to a real, already-implemented library call — see the plan's Stage 5).
- Five substantial subsystems still have no UI or CLI entry point: the Postman and curl importers,
  `DtoSampleGenerator`, the App Insights/Datadog telemetry sources, and the Azure DevOps work-item
  client.
- OpenAPI import and the semantic (Roslyn workspace) scan tier do not exist yet — the semantic tier
  is what would give a scanned request an actual body instead of a placeholder note.
- Scanned minimal-API endpoints derive their `DeclaringType` from the containing class name, or the
  file name for genuinely top-level statements; a handler with the same name in two different
  extension classes in the same file could collide.

Separately, the enterprise and cloud integrations (Entra/WAM, NTLM/Kerberos, smart cards, PAC
proxies, TLS-inspecting proxies, App Insights, Datadog, Azure DevOps) have not been exercised
against a real tenant, proxy, monitoring backend or work item tracker.
[NEEDS_LIVE_VALIDATION.md](docs/NEEDS_LIVE_VALIDATION.md) lists each one and how to check it.
Nothing on either list should be described as working until it is.

## Licence

Apache 2.0. The patent grant matters for enterprise adoption approval.
