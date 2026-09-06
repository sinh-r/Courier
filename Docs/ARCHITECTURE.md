# Courier — architecture overview

Deliverable 9. Written for a contributor about to change something.

---

## 1. Projects

```
src/
  Courier.Core/              net10.0            no UI, no Windows API
  Courier.Scanner/           net10.0            Roslyn source analysis
  Courier.Scripting/         net10.0            Jint sandbox, pm.* shim, assertions, runner
  Courier.Telemetry/         net10.0            App Insights, Datadog, Azure DevOps
  Courier.Platform.Windows/  net10.0-windows…   credential store, cert store, WinHTTP, WAM
  Courier.Platform.Posix/    net10.0            best-effort fallbacks
  Courier.Cli/               net10.0            dotnet tool
  Courier.MSBuild/           netstandard2.0     build-task package
  Courier.App/               net10.0;…-windows  Avalonia
```

### The rule that keeps this honest

**`Courier.Core` references no UI package and no Windows-only API.** Everything platform-specific
sits behind an interface in `Core/Abstractions` with a Windows implementation and a fallback.

This is what lets a contributor on Linux work on the scanner, and it is what lets the CLI exist at
all. `ArchitectureTests` enforces it: the test reads `Courier.Core.csproj` and every file under it,
and fails on an Avalonia reference, a `net*-windows` target, or a `Microsoft.Win32` using.

### Three deviations from TECH_SPEC §2, and why

1. **`Courier.App` multi-targets `net10.0;net10.0-windows10.0.19041.0`** rather than Windows only.
   The Windows target conditionally references `Courier.Platform.Windows`; the bare one references
   Posix. Without this, TECH_SPEC §1.3's "macOS and Linux build and run, best-effort" is impossible
   — a Linux contributor could not compile the app at all.

2. **`Courier.MSBuild` shells out to the CLI.** The task targets `netstandard2.0` because MSBuild
   loads it into a host that may be .NET Framework, so it *cannot* reference the `net10.0` scanner.
   `CourierScanTask` runs `courier scan` out of process and reads its JSON report. This is the only
   design that makes SCAN-11 work under Visual Studio.

3. **No git library.** STOR-05 says "without embedding a git client", so `GitStatusSource` shells
   out to `git status --porcelain=v2 -z`. It reports exactly what the user would see at their own
   prompt, including whatever their gitignore and worktree configuration do.

Also: the solution is `Courier.slnx`, not `.sln`. It is the .NET 10 SDK default and, being small
XML rather than GUID soup, is the right choice for a project whose whole thesis is diff-friendly
files.

## 2. The four designs that cannot be retrofitted

Change these carelessly and the product's claims stop being true.

### 2.1 The egress chokepoint — `Core/Privacy/EgressGate.cs`

Every outbound socket. Owns all handler construction, records every destination with a reason,
refuses anything unregistered. `NetworkStatement` renders SEC-06's auditable report from those
records, so it cannot drift from what the code does.

**If `ArchitectureTests` fires, fix the call site, not the test.** That test is the only thing
keeping SEC-01 true across a refactor.

Handler selection lives here too, keyed on the resolved request configuration. One deviation from
TECH_SPEC §3.1: PAC-based proxying uses `SocketsHttpHandler` with the proxy resolved by
`IProxyResolver` (WinHTTP P/Invoke on Windows) rather than `WinHttpHandler`, because WinHttpHandler
cannot do HTTP/3 and CORE-01 requires it. Integrated Windows auth is the one case that still needs
`HttpClientHandler`.

### 2.2 Tab state — `App/ViewModels/TabState.cs`, `TabViewModel`, `TabCollection`

A tab is a serializable POCO, never a live control tree. Only the active tab has realized visuals;
tabs past a budget of eight serialize to SQLite and drop their editor document and response buffer.

**There must be no visual indication of suspension** (UI_SPEC §4.2). `Title`, `Method` and
`VerbBrush` stay resident precisely so the strip draws correctly without waking anything.

The tab strip is cheap for a reason worth knowing: UI_SPEC fixes tabs at 180px and collapses
overflow into `+n` rather than scrolling. The strip therefore realizes `floor(width / 180)` headers
— six or eight — whether eight tabs are open or a hundred.

Measured: 645MB at 100 tabs (budget 900MB), 0.24ms to switch to a suspended tab (budget 50ms).

### 2.3 Response rendering — `Core/Rendering/`

Written from scratch; no framework control does this.

- `JsonIndexer` runs `Utf8JsonReader` over a pooled buffer producing a **flat struct array**. A
  `JsonDocument` is never materialized for a large payload.
- `JsonTreeProjection` holds expansion as a bitset over node indices. The visible rows for any
  state are one linear walk with subtree skips.
- `BodySearch` runs over the raw `ReadOnlySpan<byte>`, never the tree. That is the only reason a
  live match count over 50MB is affordable.
- Bodies over 5MB spill to the response cache and render from a `MemoryMappedFile` view.
- Display truncates at 4MB with a banner that says so.

Measured: 303ms to index 50MB into 3.4M nodes (budget 2000ms); 9.5ms to search it.

### 2.4 Generated/overlay layering — `Core/Collections/GeneratedOverlay.cs`

Two files, joined in memory on every load by `EndpointIdentity`. Never merged on disk. See
[COLLECTION_FORMAT.md](COLLECTION_FORMAT.md) §3–4 for the identity rules and what changing them
costs.

`EndpointIdentityTests` covers rename, reorder, constraint change, version bump, catch-all markers
and namespace moves. TECH_SPEC §3.6 is right that getting this wrong makes the whole feature
hostile.

## 3. Request lifecycle

```
RequestDefinition (file, has {{variables}}, no credentials)
        │  VariableResolver          request → environment → collection → global
        │  IAuthProvider             credential from ISecretStore
        ▼
PreparedRequest   (memory only, resolved, carries the values to keep out of logs)
        │  EgressGate.Authorize      policy check + record
        │  RequestExecutor           redirects, retries, timeout (CORE-11)
        ▼
ExchangeResult    (Completed | TransportFailure | Cancelled | Refused)
        │  AssertionEvaluator, ScriptRunner
        ▼
ResponseViewModel → JsonIndex → JsonTreeProjection → virtualized rows
```

`RequestDefinition` and `PreparedRequest` are separate on purpose. The definition is what the user
edits and what is written to a file, and it contains no credentials. The prepared form is resolved,
lives only in memory, and is what the capsule exporter redacts.

`ExchangeOutcome` keeps `TransportFailure` distinct from a 500. UI_SPEC §3.3: "the request never
left" is a different kind of problem, gets a distinct glyph, and must never be flattened.

## 4. Scanner

Two tiers (SCAN-08). **Syntax first, always.**

- **Syntax** — `CSharpSyntaxTree.ParseText` over `.cs` files. No restore, no build, no NuGet. Works
  on a folder that does not compile.
- **Semantic** — `MSBuildWorkspace`, needed to walk DTO type graphs for sample bodies (SCAN-04).

TECH_SPEC §3.4: "Semantic-only would break on most real codebases." A scanner that needs a green
build before it says anything is one nobody gets to try.

**The rule that matters more than the parsing:** an endpoint that cannot be derived is *listed as
unresolved with a reason*, never omitted. The subtle version of this bug is worth knowing about —
an action whose route comes from a constant the syntax tier cannot read must not silently inherit
the controller prefix, because it then collides with another action and one of them disappears.
`samples/Legacy.Api` exists to catch exactly that.

Incremental rescan is SHA-256 per file. Measured: 7.4ms for 200 endpoints (budget 3000ms).

## 5. UI

- **Design system first.** `Themes/Tokens.axaml` holds the exact palette; `Themes/Controls.axaml`
  restyles every control. Base theme is `Avalonia.Themes.Simple`, not Fluent, because Fluent's
  rounded, shadowed, accent-filled defaults are what UI_SPEC §7 puts out of bounds.
- **Colour is data.** Chrome is achromatic. Colour appears only for HTTP verbs, status classes,
  trust, diff polarity and pass/fail — and **always with a glyph or label**, never alone.
- **Fonts are embedded.** IBM Plex Sans and Mono ship as assets. NFR-06 requires air-gapped
  operation, so a webfont link would be a bug.
- **Converters are classes in one file.** `Services/Converters.cs` is the only place a verb, status
  or trust flag maps to a colour.
- **Startup is deferred.** `StartupJobQueue` runs everything non-essential after the first frame at
  background priority. Adding work to the constructor graph instead is how PERF-01 gets lost.

UI_SPEC §8's open questions ship as preferences rather than decisions: the inspector can be stacked
or tabbed, and history can live in the inspector or as a fourth pane.

## 6. Testing

| Suite | What it protects |
|---|---|
| `Courier.Core.Tests` | Architecture rules, JSON indexer, capsule redaction as a raw-bytes property |
| `Courier.Scanner.Tests` | Golden tests against `samples/`, endpoint identity, the long tail |
| `Courier.Scripting.Tests` | Sandbox escapes, the published `pm.*` surface |
| `Courier.Perf.Tests` | REQUIREMENTS §6.1 budgets, as a release gate |

`xunit.v3` runs on Microsoft.Testing.Platform, where a test project is an executable. On this SDK
`dotnet test` reports "Zero tests ran" against them, so use `dotnet run --project <test project>`,
which is the platform-native path. `build/Run-Tests.ps1` does this for all of them.

The perf runner measures the **published** build when one exists. A framework-dependent
`dotnet build` output misses PERF-01 by a factor of five, and measuring it would be measuring a
configuration nobody ships.

## 7. CI and releases

`.github/workflows/ci.yml` — three jobs. `build` is the gate: warnings-as-errors and every test.
`cross-platform` builds the platform-neutral projects on Linux, which is what stops TECH_SPEC
§1.3's promise quietly rotting. `budgets` reports REQUIREMENTS §6.1 without failing on them; the
comment on that job explains why, and names the one budget currently unmet.

`.github/workflows/release.yml` — tag `v*` to publish. Order is load-bearing:

```
Publish -> Upload unsigned -> Sign -> Attest -> Compute SHA256 -> Create release
```

Signing changes the file, so it must precede **both** the attestation and the hash. Sign after
either and the attestation covers a digest nobody can download, or the published `.sha256` does
not match the published binary.

Two things learned the hard way, both worth not relearning:

- **`continue-on-error` is not "this step may fail".** It keeps the run alive but still records
  the step as failed, and `success()` — the implicit condition on every later step — is then false.
  Four release attempts were lost to a diagnostic step that did nothing but list a directory: it
  failed, and silently skipped the sign, attest, hash and release steps after it. To make a step
  genuinely optional, swallow the failure inside it and `exit 0`.
- **Publish once.** Publishing the same project twice with different single-file settings shares
  `obj/`, and the incremental state does not survive the difference. `build/publish.ps1` produces
  one self-extracting exe and zips that; it also emits the artifact paths as step outputs so no
  workflow step reconstructs them by hand.

## 8. Where to be careful

| Change | Read first |
|---|---|
| Anything opening a socket | §2.1, and `ArchitectureTests` |
| `EndpointIdentity` | COLLECTION_FORMAT §4 — it orphans every overlay |
| Tab view-model shape | §2.2 — PERF-02 and PERF-03 depend on it |
| Adding a `pm.*` member | REQUIREMENTS §9 — the surface is a published promise, not a wishlist |
| Redaction defaults | CAPSULE_FORMAT §7 — the bias toward false positives is deliberate |
| Anything writing a file | SEC-07, and the secret-leak guard |
