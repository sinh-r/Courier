# Contributing to Courier

Thanks for looking. The project is pre-alpha: every requirement has an implementation, but the
enterprise and cloud integrations have never talked to a real tenant, proxy or monitoring backend.
**Please open an issue before starting anything substantial** — check
[`docs/NEEDS_LIVE_VALIDATION.md`](docs/NEEDS_LIVE_VALIDATION.md) first, because the area you are
looking at may be unverified rather than unwritten.

## Getting set up

Requires the .NET 10 SDK (exact version pinned in `global.json`).

```
dotnet build Courier.slnx
./build/Run-Tests.ps1
```

Use `build/Run-Tests.ps1`, not `dotnet test` — see the script header for why the latter reports
zero tests on this toolchain.

To run the app:

```
dotnet run --project src/Courier.App -f net10.0-windows10.0.19041.0
```

To measure the performance budgets, publish first. A framework-dependent build misses PERF-01 by
roughly a factor of five, and measuring it would be measuring a configuration nobody ships:

```
./build/publish.ps1 -Runtime win-x64
$env:COURIER_PUBLISH_DIR = "$PWD/artifacts/app/win-x64"
dotnet run --project tests/Courier.Perf.Tests -c Release
```

## Before you write code

Read [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and
[`Docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) for the numbered requirements the code refers to.
Most of the non-obvious constraints here are load-bearing. A few worth knowing up front:

- **Only `EgressGate` opens a socket.** Every outbound connection in the process goes through
  `Courier.Core.Privacy.EgressGate`, which records the destination and refuses anything no user
  action registered. `ArchitectureTests` fails the build if `new HttpClient`,
  `new SocketsHttpHandler`, `new HttpClientHandler` or `new Socket` appears anywhere else. If it
  fires, fix the call site, not the test — it is the only thing keeping SEC-01 true across a
  refactor.
- **`Courier.Core` has no UI and no Windows-only API.** A test asserts this against the project
  file and the sources. Everything platform-specific sits behind an interface in
  `Core/Abstractions`. This is what lets a Linux contributor work on the scanner and what lets the
  CLI exist at all.
- **A tab is a serializable POCO, never a live control tree.** PERF-02 allows 900MB at 100 tabs and
  PERF-03 allows a 50ms switch; both depend on inactive tabs serializing to SQLite and dropping
  their editor and response buffer. And there must be **no visual indication of suspension** — the
  user should never learn the concept exists.
- **Never materialize a `JsonDocument` for a response body.** `JsonIndexer` builds a flat array of
  node offsets and search runs over the raw bytes. That is the only reason a live match count over
  a 50MB payload is affordable.
- **Never change `EndpointIdentity` casually.** It is the join key between generated endpoints and
  the user's saved payloads. Changing what goes into the hash orphans every existing overlay —
  treat it as a file format change and version it. See
  [`docs/COLLECTION_FORMAT.md`](docs/COLLECTION_FORMAT.md) §4.
- **No secret ever reaches a file Courier writes.** Credentials go through `ISecretStore` to the OS
  credential store. Logs pass through pattern scrubbing *and* a registry of known secret values,
  at every verbosity level.
- **Colour is data.** The interface is achromatic; colour appears only for HTTP verbs, status
  classes, trust, diff polarity and pass/fail — and always with a glyph or label, never alone.
- **Do not add a `pm.*` member on request.** The supported surface is a published promise, not a
  wishlist. Unsupported members must fail by name.

## Style

- Warnings are errors, repo-wide. Keep the build clean rather than suppressing.
- Nullable reference types are enabled. Do not `!` your way past a real nullability issue.
- Package versions are pinned centrally in `Directory.Packages.props`, and are never floated. Add
  the version there and a bare `<PackageReference Include="..." />` in the project.
- Match the surrounding code. Comments here explain *why* something is counter-intuitive, not what
  the line does — several record a measurement or a specific failure that justifies an unusual
  choice, so please do not delete them without re-establishing the reason.

## Pull requests

- One logical change per PR, with tests.
- Say what you measured if the change touches the response viewer, the tab model, startup or the
  scanner. Those four have explicit budgets in REQUIREMENTS §6.1 and the CI run reports them.
- If you touch anything that opens a socket, writes a file, or handles a credential, say in the PR
  description which of SEC-01 through SEC-07 you considered.
- Do not commit tokens, connection strings, client secrets or certificates — not as fixtures, not
  as defaults, not in test data. The redaction engine is not a substitute for not committing them.

## Reporting bugs

Use the issue templates. For anything involving a corporate network — proxies, TLS inspection,
Entra, smart cards — include what sits between the machine and the target host. That is almost
always what determines the behaviour, and it is the part a stack trace does not show.
