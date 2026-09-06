# Courier capsule format

**Version 1.** Deliverable 7, including the redaction guarantee.

---

## 1. What a capsule is

A completed request and the response it produced, in one file, with credentials replaced by
placeholders that rebind to whoever opens it.

It exists to replace "please send me your curl". A tester hits a 500, exports a capsule, attaches it
to a bug; the developer opens it and reproduces in one click, without the tester ever explaining
what they did or the developer ever asking for the token.

## 2. The two guarantees

Everything else in this document follows from these.

### 2.1 Nothing secret is in the file

**No credential Courier can recognise, and no environment value at all, is written into a capsule.**

CAP-02 is explicit that the environment's *name* travels and its values never do. On top of that,
the redaction engine replaces anything that looks like a credential before a single byte is written,
and the export screen shows the user exactly what it replaced (CAP-04).

This is tested as a property, not as a behaviour: `CapsuleRedactionTests` writes a capsule
containing a JWT, an API key and an email address, then scans **the produced archive's raw bytes**
for all three. Testing the model would prove nothing — the guarantee is about the file.

### 2.2 A capsule is inert data

**Opening one never executes a script, never sends a request, and never writes outside Courier's own
storage.** CAP-09.

This is a security property, and the format is shaped to support it: there is no field that could
carry executable content, nothing in `CapsuleArchive` evaluates anything it reads, and entry names
are validated before any path is derived from them. A capsule arrives from someone else — often
through a bug tracker, sometimes from outside the team — and is treated accordingly.

## 3. Container

A zip archive with a registered extension of `.capsule`.

```
manifest.json      what this is, and where it came from
request.json       method, URL, headers and body as sent, redacted
response.json      status, headers and body as received, redacted
redactions.json    everything that was replaced, and why
README.txt         plain-text explanation for someone without Courier
```

For a sequence (CAP-07), `request.json` and `response.json` are replaced by:

```
steps/01/request.json
steps/01/response.json
steps/01/step.json     ordinal, name, and the variables this step exported to later ones
steps/02/...
```

Rename it to `.zip` and it opens anywhere. That is deliberate: a format the recipient cannot inspect
without installing something is a format they will not trust.

## 4. `manifest.json`

```json
{
  "capsule": 1,
  "title": "POST /api/v2/orders",
  "capturedUtc": "2026-09-06T14:02:11+00:00",
  "environmentName": "QA-Internal",
  "traceId": "4a1f8e2bc92",
  "clientVersion": "0.1.0",
  "collectionName": "Orders.Api",
  "note": "Fails only when the customer has no default address.",
  "exportedBy": "R. Sinha",
  "stepCount": 1
}
```

`environmentName` is a name and nothing else. There is no field for environment values, and adding
one would break CAP-02.

`exportedBy` is absent unless the user chose to fill it in. Courier does not read it from the OS.

## 5. `request.json` and `response.json`

```json
{
  "method": "POST",
  "url": "https://qa.internal/api/v2/orders",
  "contentType": "application/json",
  "headers": [
    { "name": "Authorization", "value": "{{auth.token}}" },
    { "name": "X-Api-Key",     "value": "{{secret.apiKey}}" },
    { "name": "Accept",        "value": "application/json" }
  ],
  "body": "{ \"customer\": { \"pan\": \"{{secret.pan}}\" } }",
  "bodyIsBase64": false
}
```

`bodyIsBase64` is set when the payload is not valid UTF-8. A base64 body is **not** scanned for
secrets — the redaction engine cannot see inside it — so binary bodies are flagged for a decision
rather than assumed safe.

A response records a transport failure explicitly:

```json
{
  "status": 0,
  "reasonPhrase": "CertificateNotTrusted",
  "transportFailure": "The certificate presented by qa.internal is not trusted by this machine.",
  "elapsedMilliseconds": 2900
}
```

"The request never left" is a different kind of problem from "the server said no", and it is often
the whole reason the capsule exists, so it travels rather than being flattened into a status of 0.

## 6. `redactions.json`

The report the recipient reads to know what changed.

```json
{
  "entries": [
    {
      "location": "Authorization",
      "placeholder": "{{auth.token}}",
      "reason": "looks like a JSON web token",
      "wasAutomatic": true
    },
    {
      "location": "body.customer.email",
      "placeholder": "{{redacted}}",
      "reason": "looks like personal data",
      "wasAutomatic": false
    }
  ]
}
```

`wasAutomatic: false` means a human decided. CAP-04 requires a decision on possible personal data,
and this field records that one was made.

## 7. Redaction

Three tiers, treated differently because they need different things from the user.

| Tier | Treatment | User action |
|---|---|---|
| **Included as-is** | Neutral | None |
| **Auto-replaced** | Trust colours, reason shown | Check the working |
| **Flagged for decision** | Heaviest weight on screen | Required: Redact or Keep |

### What is detected

- JSON web tokens, bearer tokens, connection strings, private key blocks
- AWS access key ids, GitHub tokens, Slack tokens
- Field names that name a credential: `authorization`, `x-api-key`, `password`, `clientSecret`, …
- High-entropy values: 32+ characters, mixed case and digits, no spaces
- Values Courier *knows* are secret because it read them from the credential store — this is what
  catches a short API key that no pattern would

### What is flagged rather than replaced

Anything person-shaped: email addresses, and field names like `email`, `phone`, `dob`, `pan`,
`passport`, `ssn`, `upn`, `address`. **Courier does not guess about personal data.**

### The bias

Toward false positives, deliberately. REQUIREMENTS §9 accepts them: a wrongly-flagged field costs
one click; a missed one puts a credential in a bug tracker. A flagged item with no decision is
**redacted**, because the conservative default is the one that cannot leak.

### Placeholders rebind

Replacement is not erasure. `{{auth.token}}` binds against the importer's own environment, so the
recipient sends the same request with their own credential. CAP-06 requires showing which
placeholders resolved and which did not **before** allowing a send.

## 8. Compatibility

- `capsule` greater than the reader's version: refuse, naming both, and point at the zip.
- Unknown properties: ignore.
- An entry name containing `..`, a rooted path, or a drive letter: **refuse the whole archive.**
  Nothing in Courier extracts a capsule to disk today, but the promise in §2.2 is checked at the
  boundary so it survives a future change that does.
