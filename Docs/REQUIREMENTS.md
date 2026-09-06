# Courier — Requirements and Deliverables

> **Working codename.** "Courier" is a placeholder.
> **Status.** Draft for build planning. Requirement IDs are stable once assigned; add, don't renumber.

---

## 1. Problem

Postman removed its offline Scratch Pad in September 2023, so opening a collection now requires
signing in to a cloud account. For teams testing internal APIs that carry real customer identifiers,
this made the tool unusable overnight — collections, environment variables, tokens and saved
responses became someone else's stored data and their breach surface. The risk isn't theoretical: a
year-long CloudSEK investigation found over 30,000 internet-exposed Postman Workspace instances
leaking API keys, tokens and admin credentials through misconfigured access control, accidental
collection sharing, public repository syncing and unencrypted plaintext storage.

Postman's sync compounds it. Concurrent edits resolve last-write-wins with no merge prompt and no
conflict dialog, so overwrites happen silently; recovery through the changelog requires a paid plan,
and free-tier users have none unless they exported a JSON backup first. Meanwhile the app has grown
heavy enough that crashes, extension hangs and tab-count slowdowns are routine.

Bruno addresses most of this — offline-only, no account, collections as files under git. It does not
address the Windows enterprise stack, and no tool does.

## 2. What we are building

A local-first desktop API client for **Windows enterprise .NET teams**, differentiated on four axes:

1. **Privacy by architecture.** No account, no sync, no telemetry. Credentials stay in the OS.
2. **Enterprise auth that works.** Entra ID with broker SSO, mTLS from the certificate store,
   corporate proxies with TLS inspection, and NTLM/Kerberos integrated auth.
3. **Collections derived from source.** Roslyn reads the controllers; the collection follows the code.
4. **Reproduction, not reconstruction.** Portable capsules and telemetry-driven request rebuilding
   replace "please send me your curl."

## 3. Non-goals

Explicitly not in scope, now or later, unless this document changes:

- Any hosted service, account system or cloud sync. This is a permanent constraint, not a phase.
- Team collaboration features beyond what git and capsules provide. No workspaces, no presence.
- API design, governance, catalogues, or published documentation portals.
- Load and performance testing.
- Being cross-platform at parity. Linux and macOS are best-effort; Windows is the target.

## 4. Principles

- **P1** — If a feature requires a server we operate, it does not ship.
- **P2** — The user's secret never enters a file we write, a log we emit, or a payload we transmit
  anywhere but the target host.
- **P3** — Regenerating never destroys human work.
- **P4** — Every network destination is user-visible and user-explicable.
- **P5** — Latency is a feature. A regression in interaction latency is a P1 bug.

---

## 5. Functional requirements

### 5.1 Core client — `CORE`

| ID | Requirement |
|---|---|
| CORE-01 | Compose and send HTTP/1.1, HTTP/2 and HTTP/3 requests with arbitrary method, URL, headers, query and body. |
| CORE-02 | Body editors for JSON, XML, form-urlencoded, multipart, GraphQL, binary and raw text, with syntax highlighting, folding and format-on-demand. |
| CORE-03 | Response viewer with pretty, raw and preview modes; virtualized rendering for large payloads. |
| CORE-04 | Variable resolution with precedence: request → environment → collection → global. Show the resolved value on hover. |
| CORE-05 | Cookie jar, per-environment, inspectable and editable. |
| CORE-06 | Request history: local, searchable, retained for a configurable window, with replay. |
| CORE-07 | Tabs with suspend/restore, middle-truncated titles, overflow list, and session restore on relaunch. |
| CORE-08 | Command palette over endpoints, tabs, environments and commands. |
| CORE-09 | Import from Postman collection v2.1, OpenAPI 3.x, `.http`/`.rest` files, and curl paste. |
| CORE-10 | Export to `.http` and OpenAPI; export a request as curl or as C#/Python client code. |
| CORE-11 | Redirect, retry and timeout controls per request, inheritable from collection. |
| CORE-12 | Environment editor with a shared/local split — see SEC-03. |

### 5.2 Enterprise authentication and transport — `ENT`

| ID | Requirement |
|---|---|
| ENT-01 | Entra ID via MSAL with WAM broker: silent SSO from the machine's existing Windows sign-in. |
| ENT-02 | Entra client credentials, device code, and authorization code + PKCE flows as named presets, not hand-assembled OAuth2. |
| ENT-03 | Reuse tokens from the Azure CLI and Visual Studio caches where present. |
| ENT-04 | Decoded token panel: audience, scopes, roles, issuer, issued-at, expiry, presented as a table. |
| ENT-05 | Automatic silent token refresh before expiry; never block a send on an interactive prompt without warning. |
| ENT-06 | Generic OAuth2/OIDC, Bearer, Basic, API key and AWS SigV4 for non-Entra targets. |
| ENT-07 | NTLM and Kerberos using the current Windows identity, with no credential entry. |
| ENT-08 | Client certificates selected from the Windows certificate store, per host or per collection, including smart-card-backed certs. `.pfx` file selection remains available. |
| ENT-09 | Server certificate validation against the Windows certificate store, so corporate root CAs installed by IT are trusted without configuration. |
| ENT-10 | System proxy detection including PAC scripts and proxy authentication; per-host proxy override. |
| ENT-11 | On TLS failure, report which certificate in the chain failed and why. Offer per-host trust as an explicit, recorded exception — never a global "disable verification" toggle. |

### 5.3 Collection generation from source — `SCAN`

| ID | Requirement |
|---|---|
| SCAN-01 | Scan a folder or `.sln` and derive endpoints from attribute-routed controllers and minimal API registrations. |
| SCAN-02 | Resolve route templates including `[controller]`/`[action]` tokens, route prefixes and API versioning. |
| SCAN-03 | Bind parameters by source: route, query, header, body, form. |
| SCAN-04 | Generate sample request bodies by walking the DTO type graph, honouring validation attributes, enums and nullability so samples are valid by construction. |
| SCAN-05 | Capture declared response types per status code for later assertion generation. |
| SCAN-06 | Capture authorization requirements per endpoint — policies, roles, anonymous — and surface required scopes on the request before it is sent. |
| SCAN-07 | Derive environments and base URLs from `launchSettings.json` and `appsettings.*.json`. |
| SCAN-08 | Operate syntax-only with no restore or build required; upgrade to semantic analysis when a solution loads successfully. |
| SCAN-09 | Persist generated definitions separately from user edits, joined by a stable endpoint identity, so regeneration preserves human work (P3). |
| SCAN-10 | Incremental rescan keyed on content hash; report added, changed and removed endpoints as a reviewable diff before applying. |
| SCAN-11 | Run as a build step so the collection updates on every build without the app open. |
| SCAN-12 | Provenance marking in the tree: generated, generated-then-edited, hand-written. |

### 5.4 Scripting and tests — `TEST`

| ID | Requirement |
|---|---|
| TEST-01 | Pre-request and post-response scripting in JavaScript with a sandboxed runtime and no filesystem or process access by default. |
| TEST-02 | Run Postman `pm.*` scripts unmodified for the commonly used surface — `pm.test`, `pm.expect`, `pm.response`, `pm.environment`, `pm.variables`, `pm.request`. Report unsupported calls by name rather than failing silently. |
| TEST-03 | Declarative assertions without scripting: status, header, JSONPath value, JSON schema, response time. |
| TEST-04 | Generate assertions from the codebase's integration tests where mechanically convertible; list what was skipped and why. |
| TEST-05 | Mine test fixtures and builders for known-valid request payloads and offer them as sample bodies. |
| TEST-06 | Generate an xUnit/NUnit test stub from a saved request. |
| TEST-07 | Collection runner: ordered execution, per-request assertions, data-driven runs from CSV/JSON. |

### 5.5 Capsules — `CAP`

| ID | Requirement |
|---|---|
| CAP-01 | Export a completed request/response pair as a single portable capsule file. |
| CAP-02 | Capsule contains: request, response as received, timestamp, trace ID, environment *name*, client version, redaction report. Never environment values. |
| CAP-03 | Redact on export — replace credentials with placeholders that rebind to the importer's own environment. Detect JWTs, connection strings and known secret patterns; apply configurable field-name rules. |
| CAP-04 | Mandatory pre-export review listing everything included, everything replaced, and everything flagged as possible personal data requiring a decision. |
| CAP-05 | Import by drag-drop, file open, clipboard paste, or file association double-click. |
| CAP-06 | On import, show which placeholders resolved against the local environment and which are unbound before allowing a send. |
| CAP-07 | Capture and replay a *sequence* of requests with variables threaded between steps, not only a single call. |
| CAP-08 | Attach a capsule to an Azure DevOps work item, and open a capsule directly from a work item attachment. |
| CAP-09 | Capsules are inert data. Importing one never executes script, never sends a request, and never writes outside the app's own storage. |

### 5.6 Telemetry bridge — `TEL`

| ID | Requirement |
|---|---|
| TEL-01 | Inject W3C `traceparent` on every outbound request, configurable per collection. |
| TEL-02 | Given a trace ID, query Application Insights and display the server-side timeline — dependencies, SQL, exceptions — alongside the response. |
| TEL-03 | Same capability against Datadog, behind the same interface. |
| TEL-04 | Reconstruct a runnable request from telemetry: by trace ID, or by searching URL, time window and status code. |
| TEL-05 | Indicate reconstruction fidelity — full body available, or route and headers only. |
| TEL-06 | Authenticate to monitoring backends through the same credential handling as ENT; never store a monitoring key in a collection file. |

### 5.7 Storage, git and CLI — `STOR`

| ID | Requirement |
|---|---|
| STOR-01 | Collections stored as plain text, one file per request, in a user-chosen folder. Human-readable and diff-friendly. |
| STOR-02 | No proprietary markup language. Use a widely-parsed format so the files remain useful without this tool. |
| STOR-03 | History, response cache and telemetry results in a local database, outside the git tree. |
| STOR-04 | Secrets in the OS credential store, never in any file the application writes. |
| STOR-05 | Show git status inline in the tree — modified, untracked, conflicted — without embedding a git client. |
| STOR-06 | A CLI that runs a collection with assertions and a machine-readable report, suitable for CI. |
| STOR-07 | The CLI exposes the source scanner so collection generation can run in a pipeline without the desktop app. |

### 5.8 Privacy surface — `SEC`

| ID | Requirement |
|---|---|
| SEC-01 | Zero outbound connections other than to user-specified targets. No update check, no analytics, no crash reporting, without explicit opt-in that is off by default. |
| SEC-02 | Requests go directly from client to target. No proxy service operated by us, ever. |
| SEC-03 | Environment variables are split into shared (committed) and local (credential store), visually distinguished, with a warning when a secret-shaped value is entered in a shared slot. |
| SEC-04 | A permanently visible offline indicator in the status bar. |
| SEC-05 | A settings page listing every location where data is stored on disk, with a one-click reveal in Explorer and a destructive-clear per category. |
| SEC-06 | An auditable, exportable statement of the application's network behaviour, for security review sign-off. |
| SEC-07 | No secret value is ever written to a log file, including at verbose logging levels. |

---

## 6. Non-functional requirements

### 6.1 Performance budgets

These are acceptance criteria, not aspirations. Each is measured on a mid-range corporate laptop
(4-core, 16GB, HDD-backed corporate image), and each is a release gate.

| ID | Budget |
|---|---|
| PERF-01 | Cold start to interactive under 1.5s; warm start under 800ms. |
| PERF-02 | 100 open tabs with under 900MB working set. |
| PERF-03 | Tab switch under 50ms at 100 tabs. |
| PERF-04 | Keystroke-to-render in the body editor under 16ms at a 1MB document. |
| PERF-05 | First visible content of a 50MB JSON response under 2s; scrolling stays at 60fps. |
| PERF-06 | Incremental rescan of a 200-endpoint solution under 3s. |
| PERF-07 | Idle CPU at 0% with 100 tabs open. |

PERF-02 and PERF-03 are the differentiator. Treat them as product features with named owners.

### 6.2 Other

| ID | Requirement |
|---|---|
| NFR-01 | Windows 10 22H2 and Windows 11 x64 and ARM64. |
| NFR-02 | Single-file distribution; portable mode requiring no installer and no admin rights. |
| NFR-03 | MSI and winget packages for managed corporate deployment, with admin-configurable policy defaults. |
| NFR-04 | Full keyboard operability; screen-reader labelling on all interactive elements. |
| NFR-05 | 100%–200% display scaling. |
| NFR-06 | Works fully air-gapped. No feature degrades without internet beyond reaching the target host. |
| NFR-07 | Crash recovery restores all open tabs and unsaved edits. |
| NFR-08 | Open source under a permissive licence, with reproducible builds so the security reviewer can verify the binary. |

---

## 7. Delivery phases

Each phase must be independently useful. Nothing here is a stepping stone that ships broken.

### Phase 1 — Client core
`CORE-01..08, 11, 12` · `ENT-06` · `STOR-01..04` · `SEC-01, 02, 04, 07` · `PERF-01..07`

**Definition of done:** a developer can uninstall Postman and still do their job for simple auth.
Performance budgets are met before anything else is added. If they are not met here, they never will be.

### Phase 2 — Enterprise auth
`ENT-01..05, 07..11` · `CORE-09, 10` · `SEC-03, 05`

**Definition of done:** an engineer on a domain-joined machine behind a TLS-inspecting proxy can call
an Entra-protected internal API with client-certificate auth without typing a credential.

This is the phase that has no competitor. Ship it early enough to be the reason people switch.

### Phase 3 — Collection from code
`SCAN-01..12` · `STOR-05..07`

**Definition of done:** point at a real solution, get a working collection, rebuild, see an accurate
diff, apply it, and lose nothing you had customised.

### Phase 4 — Capsules
`CAP-01..09` · `SEC-06`

**Definition of done:** a tester who has never opened the app can produce a capsule from a failing
call and attach it to a bug, and the developer opens it and reproduces in one click.

### Phase 5 — Telemetry bridge
`TEL-01..06`

**Definition of done:** paste an operation ID from an App Insights alert and get a runnable request.

### Phase 6 — Tests
`TEST-01..07`

Last, because it has the most edge cases and the least visible payoff for a new user, but it is what
converts an individual adopter into a team adoption.

---

## 8. Deliverables

**Software**
1. Desktop application, signed, with MSI, winget and portable distributions.
2. CLI for collection running and source scanning, distributed as a dotnet tool.
3. Core library published to NuGet so the scanner and runner are reusable.
4. MSBuild integration package for build-time collection generation.

**Documentation**
5. Getting started, including migration from Postman with an honest list of what does not carry over.
6. Collection file format specification, versioned, so the format outlives the tool.
7. Capsule format specification, including the redaction guarantee.
8. Security and privacy statement: data locations, network behaviour, threat model, review checklist.
9. Contribution guide and architecture overview.

**Assets**
10. UI mockups per `UI_SPEC.md`.
11. Comparison page against Postman, Bruno and Insomnia — accurate about where the alternatives win.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Bruno already occupies "offline, git-native, no account" and is well-established. | Differentiate on the enterprise stack and on capsules, not on privacy alone. Privacy is entry price, not the pitch. |
| Scope. Six phases is multiple products. | Phase 1 must be shippable and excellent alone. Reassess after each phase; cutting Phase 5 or 6 is acceptable. |
| Roslyn scanning is a long tail — every codebase has a pattern that breaks it. | Fail visibly per endpoint, never silently. An endpoint that can't be derived is listed as unresolved, not omitted. |
| `pm.*` compatibility is a promise that invites unbounded work. | Publish a supported surface list. Report unsupported calls by name. Never claim full compatibility. |
| Capsules exfiltrate PII if redaction misses something. | Mandatory review screen, conservative defaults, flag-don't-guess for anything ambiguous. Accept false positives. |
| Windows-first limits contributor pool, which skews Mac and Linux. | Keep the core library platform-neutral with Windows specifics behind interfaces, so contributors can work without a Windows machine. |
| The performance claim is easy to make and easy to lose. | Automated budget tests in CI. A regression fails the build. |
