# Courier collection format

**Version 1.** Deliverable 6. This document is the format's specification, versioned so the files
outlive the tool (STOR-02).

---

## 1. Why this document exists

STOR-01 and STOR-02 say collections are plain text, one file per request, in a folder the user
chose, in a widely-parsed format, so the files stay useful without Courier. That promise is only
real if the format is written down. A format that exists solely as whatever the serializer happens
to emit is a format that changes when someone renames a property.

Everything below is the contract. Courier reads it, and so can anything else.

## 2. Why YAML

TECH_SPEC §3.6 chose YAML over a custom DSL, and the reasoning is worth repeating because it
constrains what may be added later:

- **Not a custom DSL.** Bruno's `.bru` format is a migration tax and a parser nobody else has.
- **Diff-readable.** A collection lives in git. A one-field change must be a one-line diff.
- **Universal.** Every language has a YAML parser. A team that stops using Courier keeps its work.

Three consequences follow, and they are enforced in `CollectionSerializer`:

| Rule | Why |
|---|---|
| No anchors or aliases | `&o0` / `*o0` is valid YAML and unreadable in review. Disabled explicitly. |
| Timestamps are ISO-8601 scalars | The default object serialization writes `Ticks`, `DayOfWeek` and eighteen more properties. Nothing else can read that back. |
| Defaults and empty collections are omitted | A file should contain what the user set, not the whole model. |

## 3. Folder layout

```
my-collection/
├─ collection.yaml               settings, shared variables, auth default
├─ endpoints.generated.yaml      machine-owned. Overwritten on every scan. Committed.
├─ endpoints.overlay.yaml        human-owned. Courier never writes to it.
├─ requests/
│  └─ *.request.yaml             one file per hand-authored request
└─ environments/
   └─ *.env.yaml                 shared values only; local values live in the credential store
```

### The two-file split

`endpoints.generated.yaml` and `endpoints.overlay.yaml` are the mechanism behind SCAN-09 and P3
("regenerating never destroys human work"). They are **never merged on disk**. The join happens in
memory on every load, keyed on endpoint identity.

That is what makes it safe to run `courier scan` on every build: the generator only ever writes the
first file, so the second cannot be lost to a race, a crash, or a bug in the diff.

## 4. Endpoint identity

The join key between the two files.

```
id = lower_hex( SHA256( verb + "\n" + normalised_route + "\n" + declaring_type )[0..16] )
```

`normalised_route` is the route template with leading and trailing slashes removed, casing folded,
and route constraints, defaults, optional markers and catch-all markers stripped from parameters:

| Written | Normalises to |
|---|---|
| `{id:int}`, `{id?}`, `{id=1}`, `{id:guid:required}` | `{id}` |
| `{*path}`, `{**path}` | `{path}` |
| `/api/Orders/` | `api/orders` |

**What this buys.** Renaming a C# method, tightening a route constraint, or reordering actions all
keep the identity, so a saved payload stays attached. Changing the verb, the parameter name, the
version segment or the controller type changes it, so a payload never silently reattaches to a
different endpoint.

**What it costs.** Moving a controller between namespaces orphans its overlay entries. They are
kept, not deleted — the request appears in the tree with a note — but they must be reattached by
hand. This is a deliberate trade: the alternative is an identity that ignores the type, and then
two controllers exposing the same route in different areas become one endpoint.

**Changing what goes into this hash orphans every existing overlay.** Treat it as a format version
change.

## 5. Files

### 5.1 `collection.yaml`

```yaml
courier: 1
name: Orders.Api
description: The orders service.
variables:                    # shared, committed. Never secrets — see SEC-03.
  baseUrl: https://qa.internal/orders
headers:                      # applied to every request unless overridden
  - name: Accept
    value: application/json
authProfile: entra-qa         # names a profile; the profile holds no secret either
settings:
  followRedirects: true
  maxRedirects: 10
  timeoutMilliseconds: 30000
  retries: 0
injectTraceParent: true       # TEL-01
scannedFrom:
  path: ../src/Orders.Api
  lastScannedUtc: 2026-09-06T07:09:13.832+00:00
  useSemanticAnalysis: false
```

### 5.2 `*.request.yaml`

```yaml
courier: 1
id: 2fb6926f6edba0453b7db5f1ad96770c   # present only for a generated endpoint
name: Get order by id
method: GET
url: '{{baseUrl}}/api/v2/orders/{id}'
provenance: Generated                   # Authored | Generated | GeneratedEdited
folder: Orders
description: Gets a single order by its identifier.
pathParams:
  id: ORD-4471
query:
  - name: include
    value: lines
    enabled: true
    description: string?
headers:
  - name: Accept
    value: application/json
    enabled: true
body:
  kind: Json                            # None|Json|Xml|FormUrlEncoded|Multipart|GraphQl|Binary|Text
  contentType: application/json
  text: |
    {
      "customerId": "{{customerId}}"
    }
auth:
  profile: entra-qa
  inheritFromCollection: false
scripts:
  preRequest: |
    pm.environment.set("nonce", Date.now());
  postResponse: |
    pm.test("status is 200", function () { pm.response.to.have.status(200); });
assertions:
  - kind: Status                        # Status|Header|JsonPath|JsonSchema|ResponseTime|BodyContains
    operator: Equals
    expected: '200'
    source: GeneratedFromContract       # Authored|GeneratedFromTests|GeneratedFromContract
settings:
  timeoutMilliseconds: 5000
requiredScopes:                          # SCAN-06, surfaced before the send
  - Orders.Read
unresolvedNotes:                         # REQUIREMENTS §9: reported, never omitted
  - The request body is a CreateOrderRequest. Load the solution to generate a sample body.
```

### 5.3 `endpoints.overlay.yaml`

Every property is nullable. **Null means "I did not change this"**, and the generated value flows
through. That is what lets a regenerated route or a new header arrive while a saved payload stays.

```yaml
courier: 1
entries:
  - id: 2fb6926f6edba0453b7db5f1ad96770c
    body:
      kind: Json
      text: |
        { "customerId": "CUS-77120" }
    pathParams:
      id: ORD-4471
    orphaned: false     # true once the generator stops producing this endpoint
```

An entry whose endpoint disappears is marked `orphaned` and kept. The sync screen says so on every
pass: *"your saved payloads are kept"*. That line is load-bearing and must remain true.

### 5.4 `*.env.yaml`

```yaml
courier: 1
name: QA-Internal
shared:                       # committed to git
  baseUrl: https://qa.internal/orders
  customerId: CUS-77120
localNames:                   # names only — values live in the OS credential store
  - apiKey
  - clientSecret
```

**Nothing under `localNames` has a value in this file, ever.** The value is fetched from
`ISecretStore` at resolution time under the key `env/<environment>/<variable>`. A secret-shaped
value typed into `shared` is flagged inline before it can be committed (SEC-03).

## 6. Variable references

`{{name}}`, resolved with the precedence CORE-04 fixes:

```
request → environment → collection → global
```

Nested references resolve to a depth of 10, after which substitution stops. An unresolved reference
is **left as written** rather than replaced with an empty string, so the URL bar shows the gap
instead of silently sending a request with a missing path segment.

## 7. Compatibility

- A reader encountering `courier:` **greater** than the version it knows must refuse the file and
  say so, naming both versions. `CollectionSerializer` does this.
- A reader encountering unknown properties must ignore them. Adding an optional property is not a
  version change.
- Adding a value to an enum **is** a version change if an older reader would misinterpret it.

## 8. What is deliberately not in the format

| Not here | Where it lives | Why |
|---|---|---|
| Secret values | OS credential store | STOR-04, P2. No file Courier writes contains a secret. |
| Request history | SQLite in `%LOCALAPPDATA%\Courier` | STOR-03. Different data, different lifecycle, and history in git is miserable. |
| Response bodies | Response cache, outside the git tree | Same. |
| Window layout, open tabs | Session state, outside the git tree | Not the team's business. |
| Any account, workspace or sync identifier | Nowhere. There is none. | P1. |
