# Courier — Technical Specification

> **Working codename.** "Courier" is a placeholder.
> **Version currency.** Framework versions below were verified in September 2026. Package versions
> marked *(verified)* were confirmed; the rest should be pinned to latest stable at solution setup
> and then locked. Do not float versions.

---

## 1. Platform

### 1.1 Runtime — .NET 10 (LTS)

.NET 10 shipped in November 2025 as a Long Term Support release, supported until **10 November 2028**.
Both .NET 8 and .NET 9 reach end of support on **10 November 2026** — the same day — because standard-term
support was extended from 18 to 24 months, which collapsed the two dates together.

That makes this an easy call. Starting on .NET 8 would mean a forced migration roughly two months
after we started. .NET 10 is the only defensible target.

```xml
<TargetFramework>net10.0</TargetFramework>
<TargetFramework>net10.0-windows</TargetFramework>  <!-- Windows-specific projects only -->
<LangVersion>latest</LangVersion>
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<InvariantGlobalization>false</InvariantGlobalization>
```

**Do not** revisit for .NET 11 when it lands in November 2026. It is standard-term support and moving
to it would shorten our support window, not extend it. The next move is .NET 12 in late 2027.

### 1.2 UI framework — Avalonia 12.1.1 *(verified)*

Latest stable as of July 2026, with .NET 10 template support shipped in the tooling in January 2026.

**Why Avalonia over WPF.** Both would work, and WPF is the faster start if you already know it. The
deciding factors:

- The project is open source. Contributors on macOS and Linux need to be able to run it. WPF makes
  that impossible from day one.
- "Windows-first" and "Windows-only forever" are different commitments. Avalonia costs very little to
  keep the second door open.
- NativeAOT is available with constrained XAML; WPF has none, and cold start (PERF-01) is a release gate.
- AvaloniaEdit gives a real code editor — folding, large documents, syntax highlighting — which the
  body editor and script editor both need.

**Why not the alternatives.** WinUI 3: deployment friction, thin ecosystem. MAUI: not built for dense
desktop tooling. Tauri or Electron: an Electron shell would contradict the entire performance argument,
and Tauri would mean Rust plus a sidecar .NET process for Roslyn.

**The honest cost of Avalonia:** you will write more Windows interop yourself than WPF would require,
and some enterprise integration points (certificate store picker UI, WAM broker window parenting)
need a `net10.0-windows` project underneath. Budget for it.

### 1.3 Target platforms

Windows 10 22H2 and Windows 11, x64 and ARM64. macOS and Linux build and run, best-effort, without
the Windows-specific auth and certificate features.

---

## 2. Solution structure

```
Courier.sln
├── src/
│   ├── Courier.Core/                  net10.0          no UI, no Windows deps
│   │   ├── Http/                      execution pipeline, handler selection
│   │   ├── Collections/               model, file format, serialization
│   │   ├── Variables/                 resolution and precedence
│   │   ├── Auth/                      provider abstractions
│   │   ├── Capsules/                  format, redaction engine
│   │   └── Abstractions/              ISecretStore, ICertificateSource, IProxyResolver
│   │
│   ├── Courier.Scanner/               net10.0          Roslyn source analysis
│   │   ├── Syntax/                    parse-only path, no restore required
│   │   ├── Semantic/                  MSBuildWorkspace path, type graph walking
│   │   ├── Routing/                   template resolution, versioning
│   │   └── Diffing/                   incremental rescan, change sets
│   │
│   ├── Courier.Scripting/             net10.0          JS sandbox, pm.* shim
│   ├── Courier.Telemetry/             net10.0          App Insights, Datadog
│   │
│   ├── Courier.Platform.Windows/      net10.0-windows  credential store, cert store,
│   │                                                   WinHTTP proxy, WAM broker
│   ├── Courier.Platform.Posix/        net10.0          best-effort fallbacks
│   │
│   ├── Courier.Cli/                   net10.0          dotnet tool
│   ├── Courier.MSBuild/               netstandard2.0   build-task package
│   └── Courier.App/                   net10.0-windows  Avalonia
│
├── tests/
│   ├── Courier.Core.Tests/
│   ├── Courier.Scanner.Tests/         golden-file tests against sample solutions
│   ├── Courier.Perf.Tests/            BenchmarkDotNet, budget gates
│   └── Courier.App.UiTests/
└── samples/                           reference API solutions for scanner tests
```

**The rule that keeps this honest:** `Courier.Core` references no UI package and no Windows-only API.
Everything platform-specific sits behind an interface in `Abstractions` with a Windows implementation
and a fallback. This is what lets a contributor on Linux work on the scanner, and it is what lets the
CLI exist at all.

The `Courier.MSBuild` task targets `netstandard2.0` because MSBuild tasks load into the build host,
which may be .NET Framework under Visual Studio.

---

## 3. Component decisions

### 3.1 HTTP execution

`SocketsHttpHandler` is the default. Handler selection is per-request, driven by the auth profile:

| Need | Handler |
|---|---|
| Default, HTTP/2, HTTP/3 | `SocketsHttpHandler` |
| NTLM / Kerberos with current Windows identity | `HttpClientHandler` with `UseDefaultCredentials = true` |
| PAC-based proxy resolution, proxy auth | `WinHttpHandler`, or P/Invoke to WinHTTP for PAC evaluation |

Design this as a handler factory keyed on the resolved request configuration from day one. Retrofitting
handler selection after the pipeline is built is painful, and three of the four enterprise auth
requirements depend on it.

Client certificates: `X509Store(StoreName.My, StoreLocation.CurrentUser)`, attached via
`SslClientAuthenticationOptions`. Smart-card-backed certs work through CNG without extra handling as
long as you never try to export the private key.

Server certificate validation: hook `RemoteCertificateValidationCallback` to build the chain against
the Windows store explicitly, so corporate root CAs installed by IT are trusted. On failure, surface
`X509ChainStatus` per element — that is what turns ENT-11's error message from useless to actionable.

### 3.2 Authentication

- `Microsoft.Identity.Client` (MSAL.NET) with `WithBroker()` for WAM. The broker is what delivers
  silent SSO from the machine's existing sign-in, which is ENT-01, which is the feature nobody else
  has. Requires window-handle parenting from Avalonia — allow time for this.
- `Microsoft.Identity.Client.Broker` for the WAM runtime.
- `Azure.Identity` for `AzureCliCredential` and `VisualStudioCredential` token reuse (ENT-03).
- `System.IdentityModel.Tokens.Jwt` or `Microsoft.IdentityModel.JsonWebTokens` for the decode panel.
  Decode only — never validate signatures and never present a validity verdict, because we are not
  the resource server and a false "valid" is worse than no verdict.

### 3.3 Secret storage

Windows Credential Manager via P/Invoke to `CredReadW`/`CredWriteW`, wrapped behind `ISecretStore`.
DPAPI (`ProtectedData`) for larger blobs that exceed credential-blob limits.

Do not use a third-party cross-platform keyring wrapper. This is a small amount of P/Invoke, it is the
single most security-sensitive component, and the dependency surface is not worth it.

### 3.4 Source scanning

`Microsoft.CodeAnalysis.CSharp` — pin to the Roslyn version matching the .NET 10 SDK.

**Two-tier design, and the tiering matters more than the parsing.**

*Syntax tier* — `CSharpSyntaxTree.ParseText` over `.cs` files directly. No restore, no build, no NuGet
resolution. Covers attribute extraction, route templates, method signatures and parameter binding.
Works on any folder, including one that does not compile.

*Semantic tier* — `MSBuildWorkspace.OpenSolutionAsync`, requires a successful restore. Needed for
walking DTO type graphs across project boundaries, resolving inherited base controllers and following
type aliases.

Semantic-only would break on most real codebases, which do not restore cleanly on first contact.
Syntax-only cannot generate bodies. Ship syntax first, offer semantic as an upgrade the user opts into,
and degrade gracefully with a visible "couldn't resolve" marker per endpoint rather than silence.

Incremental rescan uses SHA-256 per file, same approach as Graphify.NET — reuse that logic rather than
rewriting it.

### 3.5 Scripting

**Jint** — pure C# JavaScript interpreter, no native dependencies, keeps single-file publish clean.
Slower than V8, which does not matter for scripts that run once per request.

Ship a `pm.*` compatibility shim over it implementing the documented supported surface. Unsupported
calls throw a named error identifying the missing member, so the user knows exactly what to rewrite.

Escalate to **ClearScript with V8** only if profiling shows script execution is a real bottleneck in
collection runs. The cost is roughly 20MB of native binaries per RID and the loss of a clean
single-file build, so do not pay it speculatively.

Sandbox: no filesystem, no process, no arbitrary network. Constrain `Engine` with execution timeout,
memory limit and recursion depth. A collection file is untrusted input the moment someone shares one.

### 3.6 Storage

**Collections** — YAML, one file per request, in the user's chosen folder.

Deliberately *not* a custom DSL. Bruno's `.bru` format is a migration tax and a parser nobody else has.
YAML is diff-friendly, every language can read it, and the files stay useful if this project dies.
Publish the schema and version it in frontmatter.

`YamlDotNet` for serialization. `System.Text.Json` with source generators everywhere else.

**Generated vs edited layering (SCAN-09).** Two files per collection:
- `endpoints.generated.yaml` — machine-owned, overwritten freely, git-committed
- `endpoints.overlay.yaml` — human-owned, never touched by the generator, joined by stable endpoint ID

Endpoint identity is a hash of `(verb, route template, controller type name)` — stable across body
changes and reordering, which is what makes rename detection possible in the diff. Getting this
identity wrong is the failure mode that makes the whole feature hostile, so test it hard.

**History, cache, telemetry results** — SQLite via `Microsoft.Data.Sqlite`, in
`%LOCALAPPDATA%\Courier\`, explicitly outside any git tree. Different data, different lifecycle,
and putting history in git would be miserable.

**Capsules** — zip container (`System.IO.Compression`) with `manifest.json`, `request.json`,
`response.json`, `redactions.json`. Custom extension with a registered file association.

### 3.7 Response rendering

This is the component most directly responsible for the performance claim, and no framework control
will do it. Plan to write it.

- Incremental parse with `Utf8JsonReader` over a pooled buffer; never materialize a `JsonDocument`
  for a large payload.
- Build a flat index of node offsets, not an object tree.
- Virtualized list rendering only visible rows; materialize nodes lazily on expand.
- Bodies over a threshold (say 5MB) stream to a temp file and render from a memory-mapped view.
- Search runs over the raw buffer, not the tree.

Budget real engineering time here. It is not a control you configure; it is a feature you build.

### 3.8 Telemetry bridge

`Azure.Monitor.Query` for App Insights KQL, authenticated through the same `Azure.Identity` chain used
for request auth. Datadog is a plain REST client behind the same `ITelemetrySource` interface.

Trace injection uses `System.Diagnostics.ActivitySource` so `traceparent` follows W3C format correctly.

---

## 4. Performance engineering

The budgets in `REQUIREMENTS.md` §6.1 are release gates. Concretely:

**Startup (PERF-01).** `PublishReadyToRun=true` with single-file publish. Defer all non-essential
initialization past first paint — no scanner, no telemetry client, no git status on startup. Consider
NativeAOT for the CLI immediately; for the app, only if you are willing to constrain XAML, and measure
before committing.

**Tabs (PERF-02, PERF-03).** Tab content is a view-model with a serialized document, not a live control
tree. Only the active tab has realized visuals. Inactive tabs beyond a threshold serialize their editor
state to SQLite and drop it from memory. The user must never perceive this — restoring must be under
the 50ms switch budget, which means the serialized form has to be cheap to rehydrate. Design the tab
view-model for this from the first commit; it cannot be retrofitted.

**Editing (PERF-04).** AvaloniaEdit handles large documents well, but syntax highlighting and
validation must be debounced off the UI thread. Never re-parse on every keystroke.

**CI enforcement.** `Courier.Perf.Tests` with BenchmarkDotNet, run on every PR against a fixed runner
spec. A budget regression fails the build. Without this the claim erodes within three months and you
will not notice until a user tells you.

---

## 5. Distribution

- Single-file, self-contained, per-RID: `win-x64`, `win-arm64`.
- Portable zip requiring no installer and no admin rights — this is how it gets into locked-down
  corporate environments before IT approves anything.
- MSI for managed deployment, with policy defaults readable from a machine-level config so admins can
  pre-set proxy, trust and telemetry-opt-out.
- winget manifest.
- Authenticode signing. An unsigned binary will not survive corporate application control.
- Reproducible builds with `ContinuousIntegrationBuild=true` and deterministic compilation, so the
  security reviewer can verify the shipped binary against source (NFR-08).
- **No auto-update mechanism that phones home by default.** It contradicts SEC-01. Offer an explicit,
  off-by-default update check, and rely on winget for the managed path.

---

## 6. Open decisions

| Decision | Options | Suggested |
|---|---|---|
| Collection file format | YAML vs TOML vs JSON | YAML — best diff readability, universal parsers |
| MVVM framework | CommunityToolkit.Mvvm vs ReactiveUI | CommunityToolkit — source-generated, lighter, less to learn for contributors |
| DI container | Microsoft.Extensions.DependencyInjection vs none | MEDI, but keep the app's own graph shallow; startup cost is a gate |
| Test framework | xUnit v3 vs TUnit | xUnit v3 — contributors know it |
| Licence | MIT vs Apache 2.0 | Apache 2.0 — the patent grant matters for enterprise adoption approval |
| Editor control | AvaloniaEdit vs custom | AvaloniaEdit initially; revisit only if PERF-04 fails |

## 7. Recommended first steps

1. Spike the performance-critical path before anything else: an Avalonia shell, 100 synthetic tabs, a
   50MB JSON response, measured against PERF-01 through PERF-05. If the budgets are unreachable, the
   entire product thesis needs revisiting and it is far better to learn that in week one.
2. Spike MSAL with WAM broker under Avalonia. Window-handle parenting is the most likely place to hit
   a wall that changes the UI framework decision.
3. Only then start the collection model and the file format, since everything else depends on it and it
   is the hardest thing to change later.
