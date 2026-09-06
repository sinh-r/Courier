# Courier — security and privacy statement

Deliverable 8: data locations, network behaviour, threat model, review checklist.

Written for the person who approves or blocks this tool. UI_SPEC §2 calls them the tertiary user:
they never use Courier, and they need to see, in the product, exactly what is stored where and what
crosses the network.

---

## 1. The short version

| Question | Answer |
|---|---|
| Does it have an account? | No. There is no account system and no server the project operates. |
| Does it sync? | No. Nothing leaves the machine except requests to hosts the user named. |
| Does it phone home? | No. No update check, no analytics, no crash reporting — none of them exist, opt-in or otherwise. |
| Where are credentials? | Windows Credential Manager. No file Courier writes contains a secret value. |
| Where are collections? | A folder the user chose. Plain YAML. Courier never copies them elsewhere. |
| Can it work air-gapped? | Yes, fully. No feature degrades without internet beyond reaching the target host. |
| Is the network behaviour auditable? | Yes, and the statement is generated from the code's own records. See §4. |

## 2. Architecture, not policy

The privacy properties are structural. There is no setting to get wrong.

### 2.1 One socket chokepoint

Every outbound connection in the process is opened by one class,
`Courier.Core.Privacy.EgressGate`. It owns the only construction of `SocketsHttpHandler` and
`HttpClientHandler` in the codebase, records `(host, port, scheme, purpose, initiator, timestamp)`
for every call, and refuses any destination that no user action registered.

**This is enforced at build time.** `Courier.Core.Tests.ArchitectureTests` scans every source file
and fails the build if `new HttpClient`, `new SocketsHttpHandler`, `new HttpClientHandler`,
`new WinHttpHandler`, `new Socket` or `new ClientWebSocket` appears outside that one file.

Libraries that insist on owning their transport — MSAL, Azure.Identity, Azure.Monitor.Query, the
Azure DevOps client — are handed an `HttpClient` from the gate rather than allowed to construct
their own. Without that, a token endpoint would be a connection the statement never mentions.

### 2.2 Six reasons a connection can exist

Every call declares one. Anything else is refused.

| Purpose | When |
|---|---|
| `TargetRequest` | A request the user composed and pressed Send on. |
| `AuthAuthority` | A token endpoint named by an auth profile the user configured. |
| `TelemetryBackend` | A monitoring backend the user connected. |
| `WorkItemTracker` | Only while attaching or opening a capsule from a work item. |
| `ProxyAutoConfig` | A PAC script named by this machine's own proxy settings. |
| `UpdateCheck` | **Off by default and refused unless switched on.** |

### 2.3 Secrets never reach a file Courier writes

`ISecretStore` is the only path to persistence for a credential. On Windows that is Credential
Manager via `CredReadW`/`CredWriteW`, with DPAPI under the current user for values exceeding the
2560-byte credential blob limit. No third-party keyring wrapper: this is the most
security-sensitive component in the product and its dependency surface is one P/Invoke.

Two further guards:

- **Log redaction.** Every log line passes through pattern scrubbing *and* a registry of literal
  values Courier knows are secret because it read them out of the store. Pattern matching alone
  misses a password like `letmein`; the registry catches it. This holds at every verbosity level
  (SEC-07).
- **A build-time check** that no `File.Write*` call takes an argument named for a credential.

### 2.4 Scripts are sandboxed

A collection or a capsule is untrusted input the moment someone shares one, and both can carry
JavaScript. The Jint engine runs with CLR interop **off**, string compilation disabled, and limits
on wall time, memory, statement count and recursion depth. `ScriptSandboxTests` asserts that
`System.IO.File`, `importNamespace`, `require`, `process`, `eval` and the `Function` constructor are
all unreachable.

## 3. Where data is stored

The application lists this at Settings → Storage with a reveal and a per-category clear (SEC-05).

| Category | Location | Contents |
|---|---|---|
| Collections | The folder you chose | Plain YAML, one file per request. Yours to move, commit and diff. |
| History and cache | `%LOCALAPPDATA%\Courier\courier.db` | Request history, cached responses, telemetry results. Outside any git tree. |
| Response cache | `%LOCALAPPDATA%\Courier\cache` | Response bodies too large to hold in memory. |
| Session state | `%LOCALAPPDATA%\Courier\session` | Open tabs and unsaved edits, for crash recovery. |
| Logs | `%LOCALAPPDATA%\Courier\logs` | Diagnostics. Secret values are removed before writing. |
| Settings | `%LOCALAPPDATA%\Courier\settings.json` | Preferences and layout. No credentials. |
| Capsule inbox | `%LOCALAPPDATA%\Courier\capsules` | Imported capsules. Inert data. |
| Secrets | Windows Credential Manager | Under the `Courier:` prefix. Never in a file. |

There is no other location. `StorageLocations.Describe()` is the single source for this table, the
settings page and the exported statement, so a new store cannot be added without appearing in all
three.

## 4. Auditable network statement

Settings → Storage → **Export statement** produces a plain-text report (SEC-06) containing:

- The architectural guarantees above, with the update-check setting's *current* value
- Every destination contacted this session, grouped by purpose, with counts, the component that
  requested each, first and last contact, and the reason for any refusal
- Every destination this profile authorises
- The storage table from §3

It is rendered from `EgressGate`'s own records, not written by hand, so it cannot claim something
the code does not do. If a category appears that you did not expect, the process really did open
that connection.

## 5. Threat model

### In scope

| Threat | Mitigation |
|---|---|
| Collections or tokens reaching a vendor cloud | No account, no sync, no server. Structurally impossible. |
| A credential committed to git | Shared/local environment split; secret-shaped values in the shared column flagged before commit; secrets only in the OS store. |
| A credential leaked through a shared capsule | Mandatory redaction review; conservative defaults; the raw-bytes test in `CapsuleRedactionTests`. |
| A credential leaked through a log | Pattern scrubbing plus a known-value registry, at all levels. |
| A malicious shared collection or capsule executing code | Jint sandbox with no CLR, no filesystem, no process, no string compilation; capsules carry no executable field. |
| A malicious capsule writing outside app storage | Entry names validated; the archive is refused on any traversal attempt. |
| Silent TLS downgrade | No global "disable verification" exists. Per-host exceptions only, recorded with a timestamp and thumbprint, revoked automatically when the certificate changes. |
| Undisclosed outbound traffic | One chokepoint, build-time enforced, plus the generated statement. |

### Out of scope, stated plainly

- **A compromised machine.** Courier holds credentials in the OS store; anything running as the
  user can ask the OS for them. This is true of every client on the platform.
- **A malicious target host.** Courier sends what the user composed to the host the user named.
- **The user choosing to share a secret.** The export review shows what is included; a user who
  presses Keep on a flagged value has made a decision Courier records and honours.
- **Supply chain of pinned dependencies.** Versions are pinned and never floated
  (`Directory.Packages.props`), and builds are deterministic so the shipped binary can be verified
  against source (NFR-08) — but the packages themselves are trusted.

## 6. Review checklist

For sign-off. Each item is verifiable without trusting this document.

- [ ] **No account.** Launch the app. There is no sign-in, no workspace, no profile.
- [ ] **Offline indicator.** The status bar reads `Offline · no sync` and does not scroll away.
- [ ] **Air-gapped.** Disconnect the network. The app starts, opens collections, and reports only
      target-host failures.
- [ ] **Egress chokepoint.** `grep -rn "new HttpClient\|new SocketsHttpHandler" src/` returns only
      `EgressGate.cs`. Then run `Courier.Core.Tests` and confirm `ArchitectureTests` passes.
- [ ] **Network statement.** Send one request, export the statement, confirm it lists that host and
      nothing else.
- [ ] **Secrets.** Set a local environment variable. Confirm it appears in Credential Manager under
      `Courier:` and `grep -r` finds it in no file under the collection folder or `%LOCALAPPDATA%`.
- [ ] **Log redaction.** Run at verbose level, then grep the logs for the secret's value.
- [ ] **Capsule redaction.** Export a capsule from a request carrying a bearer token. Unzip it and
      grep the raw bytes for the token.
- [ ] **Capsule inertness.** Import a capsule whose script would write a file. Confirm nothing runs
      and nothing is sent until Send is pressed.
- [ ] **Script sandbox.** Run `Courier.Scripting.Tests` and read `ScriptSandboxTests`.
- [ ] **No update check.** Confirm there is no setting enabling one by default, and that the
      statement reports it OFF.
- [ ] **Reproducible build.** Build twice from a clean tree and compare the binaries.

## 7. Reporting a vulnerability

Courier is open source under Apache 2.0. Report security issues through the repository's private
vulnerability reporting rather than a public issue.
