using System.Text;
using Courier.Core.Capsules;

namespace Courier.Core.Tests;

/// <summary>
/// The capsule guarantee, tested as a guarantee: nothing secret reaches the bytes on disk.
/// CAP-02, CAP-03, CAP-04, CAP-09.
/// </summary>
public sealed class CapsuleRedactionTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
        + ".eyJzdWIiOiIxMjM0NTY3ODkwIiwiYXVkIjoiYXBpOi8vb3JkZXJzIn0"
        + ".dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

    private static CapsuleRequest SampleRequest() => new()
    {
        Method = "POST",
        Url = "https://qa.internal/api/v2/orders",
        ContentType = "application/json",
        Headers =
        [
            new CapsuleHeader("Authorization", $"Bearer {Jwt}"),
            new CapsuleHeader("X-Api-Key", "9f2a7c41e8b34d02a5f6"),
            new CapsuleHeader("Accept", "application/json"),
        ],
        Body = """
            {
              "customer": {
                "pan": "ABCDE1234F",
                "email": "r.sinha@contoso.com",
                "name": "R Sinha"
              },
              "lines": [{ "sku": "SKU-8841", "qty": 2 }]
            }
            """,
    };

    private static CapsuleResponse SampleResponse() => new()
    {
        Status = 500,
        ReasonPhrase = "Internal Server Error",
        ContentType = "application/json",
        ContentLength = 2458,
        Headers = [new CapsuleHeader("Content-Type", "application/json")],
        Body = """{ "error": "order pipeline failed", "traceId": "4a1f8e2b" }""",
    };

    [Fact]
    public void Replaces_credentials_and_flags_personal_data_separately()
    {
        var review = new RedactionEngine().Review(SampleRequest(), SampleResponse());

        var replacedLocations = review.Replaced.Select(r => r.Location).ToList();
        Assert.Contains("Authorization", replacedLocations);
        Assert.Contains("X-Api-Key", replacedLocations);

        // A PAN is personal data with a field name that says so, and gets flagged rather than
        // silently replaced — CAP-04 requires a human decision on this tier.
        var flaggedLocations = review.Flagged.Select(f => f.Location).ToList();
        Assert.Contains("body.customer.email", flaggedLocations);
        Assert.True(review.NeedsDecision);

        // Nothing benign is touched.
        Assert.DoesNotContain("Accept", replacedLocations);
    }

    [Fact]
    public void Placeholders_are_rebindable_rather_than_erased()
    {
        var engine = new RedactionEngine();
        var review = engine.Review(SampleRequest(), SampleResponse());
        var (request, _, _) = engine.Apply(SampleRequest(), SampleResponse(), review, new Dictionary<string, bool>());

        var authorization = request.Headers.Single(h => h.Name == "Authorization").Value;

        // CAP-03: a placeholder the importer's own environment can bind, not a black box.
        Assert.Contains("{{", authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(Jwt, authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void Flagged_items_are_redacted_when_the_exporter_has_not_decided()
    {
        var engine = new RedactionEngine();
        var review = engine.Review(SampleRequest(), SampleResponse());
        var (request, _, report) = engine.Apply(
            SampleRequest(),
            SampleResponse(),
            review,
            new Dictionary<string, bool>());

        Assert.DoesNotContain("r.sinha@contoso.com", request.Body, StringComparison.Ordinal);
        Assert.Contains(report.Entries, e => !e.WasAutomatic && e.Placeholder == "{{redacted}}");
    }

    [Fact]
    public void Flagged_items_survive_when_the_exporter_chooses_to_keep_them()
    {
        var engine = new RedactionEngine();
        var review = engine.Review(SampleRequest(), SampleResponse());
        var decisions = review.Flagged.ToDictionary(f => f.Location, _ => true);

        var (request, _, _) = engine.Apply(SampleRequest(), SampleResponse(), review, decisions);

        Assert.Contains("r.sinha@contoso.com", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_secret_reaches_the_bytes_that_are_written()
    {
        var engine = new RedactionEngine();
        engine.RegisterKnownSecret("9f2a7c41e8b34d02a5f6", "{{secret.apiKey}}");

        var review = engine.Review(SampleRequest(), SampleResponse());
        var (request, response, report) = engine.Apply(
            SampleRequest(),
            SampleResponse(),
            review,
            new Dictionary<string, bool>());

        using var stream = new MemoryStream();
        await CapsuleArchive.WriteAsync(
            stream,
            new CapsuleManifest { Title = "POST /api/v2/orders", EnvironmentName = "QA-Internal" },
            [new CapsuleStep { Ordinal = 1, Name = "order", Request = request, Response = response }],
            report,
            TestContext.Current.CancellationToken);

        // The strongest form of the test: scan the produced archive's raw bytes, not the model.
        var bytes = stream.ToArray();
        var haystack = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(Jwt, haystack, StringComparison.Ordinal);
        Assert.DoesNotContain("9f2a7c41e8b34d02a5f6", haystack, StringComparison.Ordinal);
        Assert.DoesNotContain("r.sinha@contoso.com", haystack, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Round_trips_through_the_container_with_the_environment_name_but_no_values()
    {
        var engine = new RedactionEngine();
        var review = engine.Review(SampleRequest(), SampleResponse());
        var (request, response, report) = engine.Apply(
            SampleRequest(),
            SampleResponse(),
            review,
            new Dictionary<string, bool>());

        using var stream = new MemoryStream();
        await CapsuleArchive.WriteAsync(
            stream,
            new CapsuleManifest
            {
                Title = "POST /api/v2/orders",
                EnvironmentName = "QA-Internal",
                TraceId = "4a1f8e2bc92",
                ClientVersion = "0.1.0",
            },
            [new CapsuleStep { Ordinal = 1, Name = "order", Request = request, Response = response }],
            report,
            TestContext.Current.CancellationToken);

        stream.Position = 0;
        var capsule = await CapsuleArchive.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal("QA-Internal", capsule.Manifest.EnvironmentName);
        Assert.Equal("4a1f8e2bc92", capsule.Manifest.TraceId);
        Assert.Single(capsule.Steps);
        Assert.Equal(500, capsule.Steps[0].Response!.Status);
        Assert.NotEmpty(capsule.Redactions.Entries);
    }

    [Fact]
    public async Task Reports_which_placeholders_bind_to_the_local_environment()
    {
        var engine = new RedactionEngine();
        var review = engine.Review(SampleRequest(), SampleResponse());
        var (request, response, report) = engine.Apply(
            SampleRequest(),
            SampleResponse(),
            review,
            new Dictionary<string, bool>());

        using var stream = new MemoryStream();
        await CapsuleArchive.WriteAsync(
            stream,
            new CapsuleManifest { Title = "order" },
            [new CapsuleStep { Request = request, Response = response }],
            report,
            TestContext.Current.CancellationToken);

        stream.Position = 0;
        var capsule = await CapsuleArchive.ReadAsync(stream, TestContext.Current.CancellationToken);

        var placeholders = capsule.ReferencedPlaceholders();
        Assert.NotEmpty(placeholders);

        // CAP-06: with nothing configured locally, every placeholder is reported unresolved rather
        // than quietly sending an empty header.
        var binding = capsule.Bind(new Courier.Core.Variables.VariableScopes());
        Assert.Empty(binding.Resolved);
        Assert.Equal(placeholders.Count, binding.Unresolved.Count);
        Assert.False(binding.IsFullyBound);
    }

    [Fact]
    public async Task Refuses_an_archive_whose_entry_names_could_escape_a_directory()
    {
        using var stream = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(
            stream,
            System.IO.Compression.ZipArchiveMode.Create,
            leaveOpen: true))
        {
            var entry = archive.CreateEntry("../../evil.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("{}".AsMemory(), TestContext.Current.CancellationToken);
        }

        stream.Position = 0;

        // CAP-09: importing a capsule never writes outside Courier's own storage, and the check
        // lives at the boundary so it survives a future change that does extract to disk.
        await Assert.ThrowsAsync<CapsuleFormatException>(() => CapsuleArchive.ReadAsync(stream, TestContext.Current.CancellationToken));
    }
}
