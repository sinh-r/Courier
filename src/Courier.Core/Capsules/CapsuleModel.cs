using System.Text.Json.Serialization;

namespace Courier.Core.Capsules;

/// <summary>
/// Constants of the capsule container. TECH_SPEC 3.6: a zip with a manifest, the request, the
/// response and the redaction report.
/// </summary>
public static class CapsuleFormat
{
    public const int Version = 1;

    public const string Extension = ".capsule";

    public const string ManifestEntry = "manifest.json";
    public const string RequestEntry = "request.json";
    public const string ResponseEntry = "response.json";
    public const string RedactionsEntry = "redactions.json";
    public const string ReadmeEntry = "README.txt";

    /// <summary>Written into every capsule so a recipient without Courier can still read it.</summary>
    public const string Readme = """
        This is a Courier capsule: a request, the response it produced, and the trace id that ties
        them to server-side telemetry.

        It is a zip file. Rename it to .zip and open it with anything.

          manifest.json    what this capsule is, and where it came from
          request.json     method, URL, headers and body as sent, with secrets replaced
          response.json    status, headers and body as received
          redactions.json  everything that was replaced, and what it was replaced with

        Environment values are not included. Credentials were replaced with placeholders before
        this file was written; the redaction report lists every one of them.

        A capsule is inert data. Opening one never runs a script and never sends a request.
        """;
}

/// <summary>
/// What the capsule is. CAP-02: request, response, timestamp, trace id, environment <i>name</i>,
/// client version, redaction report. Never environment values.
/// </summary>
public sealed class CapsuleManifest
{
    [JsonPropertyName("capsule")]
    public int Version { get; set; } = CapsuleFormat.Version;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CapturedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The name only. CAP-02 is explicit that values never travel.</summary>
    public string? EnvironmentName { get; set; }

    public string? TraceId { get; set; }

    public string ClientVersion { get; set; } = "unknown";

    public string? CollectionName { get; set; }

    /// <summary>Free text the exporter typed. Never auto-filled from anything identifying.</summary>
    public string? Note { get; set; }

    /// <summary>Who exported it, only if they chose to say. Defaults to absent.</summary>
    public string? ExportedBy { get; set; }

    /// <summary>Number of steps. One for a single call, more for a sequence. CAP-07.</summary>
    public int StepCount { get; set; } = 1;
}

/// <summary>One request/response pair inside a capsule.</summary>
public sealed class CapsuleStep
{
    public int Ordinal { get; set; }

    public string Name { get; set; } = string.Empty;

    public CapsuleRequest Request { get; set; } = new();

    public CapsuleResponse? Response { get; set; }

    /// <summary>
    /// Variables this step's response contributed to later steps, by name only. CAP-07 threads
    /// variables between steps; the values are placeholders like everything else.
    /// </summary>
    public Dictionary<string, string> Exports { get; set; } = [];
}

public sealed class CapsuleRequest
{
    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public List<CapsuleHeader> Headers { get; set; } = [];

    public string? Body { get; set; }

    public string? ContentType { get; set; }

    /// <summary>Base64 when the body is not valid UTF-8 text.</summary>
    public bool BodyIsBase64 { get; set; }
}

public sealed class CapsuleResponse
{
    public int Status { get; set; }

    public string ReasonPhrase { get; set; } = string.Empty;

    public List<CapsuleHeader> Headers { get; set; } = [];

    public string? Body { get; set; }

    public string? ContentType { get; set; }

    public bool BodyIsBase64 { get; set; }

    public long ContentLength { get; set; }

    public double ElapsedMilliseconds { get; set; }

    /// <summary>Set when the request never left, so the recipient sees that rather than a fake 0.</summary>
    public string? TransportFailure { get; set; }
}

public sealed record CapsuleHeader(string Name, string Value);
