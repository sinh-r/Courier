using Courier.Core.Collections;

namespace Courier.Core.Tests;

/// <summary>
/// The on-disk format, tested as a round trip. STOR-01, STOR-02.
/// </summary>
/// <remarks>
/// Serializing was tested by eye and looked fine; reading it back threw, because a positional
/// record has no parameterless constructor for the deserializer to use. A collection that can be
/// written and never opened is worse than one that fails on write, so every type that reaches a
/// file is round-tripped here rather than only inspected.
/// </remarks>
public sealed class CollectionFormatTests
{
    private readonly CollectionSerializer _serializer = new();

    private static RequestDefinition FullyPopulated() => new()
    {
        Id = "2fb6926f6edba0453b7db5f1ad96770c",
        Name = "Create order",
        Method = "POST",
        Url = "{{baseUrl}}/api/v2/orders/{id}",
        Provenance = Provenance.GeneratedEdited,
        Folder = "Orders/Write",
        Description = "Creates an order.",
        PathParams = { ["id"] = "ORD-4471" },
        Query =
        {
            new QueryParameter("include", "lines", Enabled: true, "string?"),
            new QueryParameter("dryRun", "false", Enabled: false, null),
        },
        Headers =
        {
            new HeaderValue("Accept", "application/json"),
            new HeaderValue("X-Correlation-Id", "{{correlationId}}", Enabled: false, "optional"),
        },
        Body = new RequestBody
        {
            Kind = BodyKind.Json,
            ContentType = "application/json",
            Text = "{\n  \"customerId\": \"{{customerId}}\"\n}",
        },
        Auth = new AuthReference("entra-qa", InheritFromCollection: false),
        Scripts = new RequestScripts("pm.environment.set('n', 1);", "pm.test('ok', function () {});"),
        Assertions =
        {
            new AssertionDefinition(AssertionKind.Status, Expected: "201", Source: AssertionSource.GeneratedFromContract),
            new AssertionDefinition(
                AssertionKind.JsonPath,
                "$.order.total",
                AssertionOperator.Equals,
                "184.20",
                AssertionSource.Authored,
                "written by you"),
        },
        Settings = new RequestSettings { TimeoutMilliseconds = 5000, Retries = 2, HttpVersion = "2.0" },
        RequiredScopes = { "Orders.Write", "Orders.Admin" },
        UnresolvedNotes = { "The request body is a CreateOrderRequest." },
    };

    [Fact]
    public void A_request_survives_a_round_trip_intact()
    {
        var original = FullyPopulated();

        var restored = _serializer.DeserializeRequest(_serializer.SerializeRequest(original));

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Method, restored.Method);
        Assert.Equal(original.Url, restored.Url);
        Assert.Equal(original.Provenance, restored.Provenance);
        Assert.Equal(original.Folder, restored.Folder);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.PathParams, restored.PathParams);
        Assert.Equal(original.RequiredScopes, restored.RequiredScopes);
        Assert.Equal(original.UnresolvedNotes, restored.UnresolvedNotes);
    }

    [Fact]
    public void Query_parameters_and_headers_survive_including_the_disabled_ones()
    {
        var restored = _serializer.DeserializeRequest(_serializer.SerializeRequest(FullyPopulated()));

        // A disabled row stays in the file so toggling one is not destructive.
        Assert.Equal(2, restored.Query.Count);
        Assert.Equal("include", restored.Query[0].Name);
        Assert.True(restored.Query[0].Enabled);
        Assert.False(restored.Query[1].Enabled);

        Assert.Equal(2, restored.Headers.Count);
        Assert.Equal("X-Correlation-Id", restored.Headers[1].Name);
        Assert.False(restored.Headers[1].Enabled);
    }

    [Fact]
    public void The_body_survives_with_its_newlines()
    {
        var restored = _serializer.DeserializeRequest(_serializer.SerializeRequest(FullyPopulated()));

        Assert.NotNull(restored.Body);
        Assert.Equal(BodyKind.Json, restored.Body.Kind);
        Assert.Equal("application/json", restored.Body.ContentType);
        Assert.Contains("customerId", restored.Body.Text, StringComparison.Ordinal);
        Assert.Contains('\n', restored.Body.Text!);
    }

    [Fact]
    public void Auth_scripts_assertions_and_settings_survive()
    {
        var restored = _serializer.DeserializeRequest(_serializer.SerializeRequest(FullyPopulated()));

        Assert.Equal("entra-qa", restored.Auth!.Profile);
        Assert.False(restored.Auth.InheritFromCollection);

        Assert.Contains("pm.environment", restored.Scripts!.PreRequest, StringComparison.Ordinal);
        Assert.Contains("pm.test", restored.Scripts.PostResponse, StringComparison.Ordinal);

        Assert.Equal(2, restored.Assertions.Count);
        Assert.Equal(AssertionKind.JsonPath, restored.Assertions[1].Kind);
        Assert.Equal("$.order.total", restored.Assertions[1].Target);
        Assert.Equal(AssertionSource.Authored, restored.Assertions[1].Source);

        Assert.Equal(5000, restored.Settings.TimeoutMilliseconds);
        Assert.Equal(2, restored.Settings.Retries);
        Assert.Equal("2.0", restored.Settings.HttpVersion);
    }

    [Fact]
    public void A_multipart_body_survives_its_form_fields()
    {
        var original = new RequestDefinition
        {
            Name = "Upload",
            Method = "POST",
            Body = new RequestBody
            {
                Kind = BodyKind.Multipart,
                Form =
                {
                    new FormField("description", "an order export"),
                    new FormField("file", null, "C:/exports/orders.csv", "text/csv"),
                },
            },
        };

        var restored = _serializer.DeserializeRequest(_serializer.SerializeRequest(original));

        Assert.Equal(2, restored.Body!.Form.Count);
        Assert.Equal("an order export", restored.Body.Form[0].Value);
        Assert.Equal("C:/exports/orders.csv", restored.Body.Form[1].FilePath);
        Assert.Equal("text/csv", restored.Body.Form[1].ContentType);
    }

    [Fact]
    public void A_collection_survives_including_its_scan_source()
    {
        var original = new CollectionDefinition
        {
            Name = "Orders.Api",
            Description = "The orders service.",
            Variables = { ["baseUrl"] = "https://qa.internal/orders" },
            Headers = { new HeaderValue("Accept", "application/json") },
            AuthProfile = "entra-qa",
            Settings = new RequestSettings { FollowRedirects = true, MaxRedirects = 5 },
            InjectTraceParent = true,
            ScannedFrom = new ScanSource("../src/Orders.Api", DateTimeOffset.UtcNow, UseSemanticAnalysis: true),
        };

        var restored = _serializer.DeserializeCollection(_serializer.SerializeCollection(original));

        Assert.Equal("Orders.Api", restored.Name);
        Assert.Equal("https://qa.internal/orders", restored.Variables["baseUrl"]);
        Assert.Single(restored.Headers);
        Assert.Equal("entra-qa", restored.AuthProfile);
        Assert.True(restored.InjectTraceParent);
        Assert.Equal("../src/Orders.Api", restored.ScannedFrom!.Path);
        Assert.True(restored.ScannedFrom.UseSemanticAnalysis);
    }

    [Fact]
    public void Declared_folders_survive_and_an_unused_list_is_omitted()
    {
        var restored = _serializer.DeserializeCollection(_serializer.SerializeCollection(new CollectionDefinition
        {
            Name = "Orders.Api",
            Folders = ["Articles", "Articles/Nested"],
        }));

        Assert.Equal(["Articles", "Articles/Nested"], restored.Folders);

        // OmitEmptyCollections keeps a collection nobody has organized into folders yet free of a
        // stray "folders: []" line.
        var yaml = _serializer.SerializeCollection(new CollectionDefinition { Name = "Orders.Api" });
        Assert.DoesNotContain("folders:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timestamp_is_one_iso_scalar_and_reads_back()
    {
        var when = new DateTimeOffset(2026, 9, 6, 14, 2, 11, TimeSpan.Zero);
        var yaml = _serializer.SerializeCollection(new CollectionDefinition
        {
            ScannedFrom = new ScanSource("src", when),
        });

        // Without the converter, YamlDotNet writes Ticks, DayOfWeek and eighteen more properties —
        // unreadable in a diff and unparseable by anything else. STOR-02.
        Assert.Contains("2026-09-06T14:02:11", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("dayOfWeek", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticks", yaml, StringComparison.OrdinalIgnoreCase);

        var restored = _serializer.DeserializeCollection(yaml);
        Assert.Equal(when, restored.ScannedFrom!.LastScannedUtc);
    }

    [Fact]
    public void The_file_carries_no_yaml_anchors()
    {
        // "&o0 {}" and "*o0" are valid YAML and unreadable in review: the file suddenly refers to
        // a value defined forty lines earlier for no reason the user can see.
        var yaml = _serializer.SerializeGenerated(new GeneratedEndpointSet
        {
            Endpoints = [FullyPopulated(), FullyPopulated(), FullyPopulated()],
        });

        Assert.DoesNotContain(" &o", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(" *o", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_generated_set_and_its_overlay_round_trip_and_join()
    {
        var generated = new GeneratedEndpointSet
        {
            Source = "Syntax",
            Endpoints = [FullyPopulated()],
        };

        var overlay = new OverlaySet
        {
            Entries =
            [
                new OverlayEntry
                {
                    Id = generated.Endpoints[0].Id!,
                    Body = new RequestBody { Kind = BodyKind.Json, Text = "{ \"customerId\": \"CUS-77120\" }" },
                    PathParams = new Dictionary<string, string> { ["id"] = "ORD-9999" },
                },
            ],
        };

        var restoredGenerated = _serializer.DeserializeGenerated(_serializer.SerializeGenerated(generated));
        var restoredOverlay = _serializer.DeserializeOverlay(_serializer.SerializeOverlay(overlay));

        var joined = GeneratedOverlayJoiner.Join(restoredGenerated, restoredOverlay);

        // The user's saved payload wins; everything else comes from the generator. SCAN-09, P3.
        Assert.Single(joined);
        Assert.Contains("CUS-77120", joined[0].Body!.Text, StringComparison.Ordinal);
        Assert.Equal("ORD-9999", joined[0].PathParams["id"]);
        Assert.Equal("POST", joined[0].Method);
        Assert.Equal(Provenance.GeneratedEdited, joined[0].Provenance);
    }

    [Fact]
    public void An_environment_round_trips_with_local_names_but_no_local_values()
    {
        var original = new EnvironmentDefinition
        {
            Name = "QA-Internal",
            Shared = { ["baseUrl"] = "https://qa.internal/orders", ["customerId"] = "CUS-77120" },
            LocalNames = { "apiKey", "clientSecret" },
        };

        var yaml = _serializer.SerializeEnvironment(original);
        var restored = _serializer.DeserializeEnvironment(yaml);

        Assert.Equal(2, restored.Shared.Count);
        Assert.Equal(["apiKey", "clientSecret"], restored.LocalNames);

        // SEC-03 and STOR-04: the file records that a local variable exists and nothing else.
        Assert.DoesNotContain("apiKey:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_future_format_version_is_refused_with_both_versions_named()
    {
        var yaml = _serializer.SerializeRequest(new RequestDefinition { Courier = CollectionFormat.Version + 5 });

        var exception = Assert.Throws<CollectionFormatException>(() => _serializer.DeserializeRequest(yaml));

        Assert.Contains((CollectionFormat.Version + 5).ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(CollectionFormat.Version.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_property_is_ignored_rather_than_refused()
    {
        // Adding an optional property is not a version change, so an older reader must cope.
        var yaml = _serializer.SerializeRequest(FullyPopulated()) + "\nsomethingFromTheFuture: 42\n";

        var restored = _serializer.DeserializeRequest(yaml);

        Assert.Equal("Create order", restored.Name);
    }
}
