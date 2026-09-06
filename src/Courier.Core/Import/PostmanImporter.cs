using System.Text.Json;
using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Privacy;

namespace Courier.Core.Import;

/// <summary>
/// Imports a Postman collection v2.1 export. CORE-09.
/// </summary>
/// <remarks>
/// <para>
/// The migration path matters more than the feature list: someone switching has years of
/// collections and will not retype them. What they will not tolerate is a silent partial import, so
/// anything not carried over is reported by name in <see cref="ImportResult.Notes"/> — the honest
/// list REQUIREMENTS deliverable 5 asks for.
/// </para>
/// <para>
/// Secrets found in the export go to the credential store, never into the imported file. A Postman
/// export routinely contains live bearer tokens in plain text; writing those into a git-tracked
/// YAML file would reproduce the exact problem Courier exists to solve.
/// </para>
/// </remarks>
public sealed class PostmanImporter
{
    private readonly ISecretStore _secrets;

    public PostmanImporter(ISecretStore secrets) => _secrets = secrets;

    public async Task<ImportResult> ImportAsync(Stream json, CancellationToken ct = default)
    {
        using var document = await JsonDocument.ParseAsync(json, default, ct).ConfigureAwait(false);
        var root = document.RootElement;

        var notes = new List<string>();
        var requests = new List<RequestDefinition>();

        var collection = new CollectionDefinition
        {
            Name = root.TryGetProperty("info", out var info) && info.TryGetProperty("name", out var name)
                ? name.GetString() ?? "Imported collection"
                : "Imported collection",
        };

        AssertSupportedSchema(root, notes);

        if (root.TryGetProperty("variable", out var variables))
        {
            await ReadVariablesAsync(variables, collection, notes, ct).ConfigureAwait(false);
        }

        if (root.TryGetProperty("item", out var items))
        {
            await ReadItemsAsync(items, folder: null, requests, notes, ct).ConfigureAwait(false);
        }

        // Collection-level auth and scripts are the two things most often lost in a migration, so
        // they are called out explicitly rather than left for the user to discover.
        if (root.TryGetProperty("auth", out _))
        {
            notes.Add("Collection-level auth was not imported. Create an auth profile and set it as the collection default.");
        }

        if (root.TryGetProperty("event", out _))
        {
            notes.Add("Collection-level pre-request and test scripts were not imported. Copy them onto the requests that need them.");
        }

        return new ImportResult(collection, requests, [], notes);
    }

    private static void AssertSupportedSchema(JsonElement root, List<string> notes)
    {
        if (!root.TryGetProperty("info", out var info) || !info.TryGetProperty("schema", out var schema))
        {
            notes.Add("The export has no schema marker. It was read as v2.1; check the results.");
            return;
        }

        var value = schema.GetString() ?? string.Empty;

        if (!value.Contains("v2.1", StringComparison.Ordinal) && !value.Contains("v2.0", StringComparison.Ordinal))
        {
            notes.Add($"This export declares schema '{value}'. Courier reads v2.1; some fields may not carry over.");
        }
    }

    private async Task ReadVariablesAsync(
        JsonElement variables,
        CollectionDefinition collection,
        List<string> notes,
        CancellationToken ct)
    {
        foreach (var variable in variables.EnumerateArray())
        {
            var key = Text(variable, "key");
            var value = Text(variable, "value");

            if (key is null)
            {
                continue;
            }

            // SEC-03: a secret-shaped value never lands in the committed column, even on import.
            if (SecretPatterns.Classify(value, key).IsSecret)
            {
                await _secrets
                    .SetAsync(new SecretKey(SecretKey.EnvironmentScope, $"Imported/{key}"), value ?? string.Empty, ct)
                    .ConfigureAwait(false);

                notes.Add($"'{key}' looked like a secret and went to the credential store, not the collection file.");
                continue;
            }

            collection.Variables[key] = value ?? string.Empty;
        }
    }

    private async Task ReadItemsAsync(
        JsonElement items,
        string? folder,
        List<RequestDefinition> requests,
        List<string> notes,
        CancellationToken ct)
    {
        foreach (var item in items.EnumerateArray())
        {
            var name = Text(item, "name") ?? "Untitled";

            // A folder is an item with its own items. Recursion mirrors the tree the user had.
            if (item.TryGetProperty("item", out var children))
            {
                var nested = folder is null ? name : $"{folder}/{name}";
                await ReadItemsAsync(children, nested, requests, notes, ct).ConfigureAwait(false);
                continue;
            }

            if (!item.TryGetProperty("request", out var request))
            {
                continue;
            }

            requests.Add(await ReadRequestAsync(request, name, folder, notes, ct).ConfigureAwait(false));

            if (item.TryGetProperty("event", out var events))
            {
                ReadScripts(events, requests[^1]);
            }
        }
    }

    private async Task<RequestDefinition> ReadRequestAsync(
        JsonElement request,
        string name,
        string? folder,
        List<string> notes,
        CancellationToken ct)
    {
        var definition = new RequestDefinition
        {
            Name = name,
            Folder = folder,
            Method = Text(request, "method") ?? "GET",
            Provenance = Provenance.Authored,
            Description = Text(request, "description"),
        };

        definition.Url = ReadUrl(request, definition);

        if (request.TryGetProperty("header", out var headers))
        {
            foreach (var header in headers.EnumerateArray())
            {
                var key = Text(header, "key");
                var value = Text(header, "value");

                if (key is null)
                {
                    continue;
                }

                // A live token in an export goes to the credential store and leaves a reference.
                if (SecretPatterns.Classify(value, key).IsSecret)
                {
                    var secretKey = new SecretKey(SecretKey.EnvironmentScope, $"Imported/{key}");
                    await _secrets.SetAsync(secretKey, value ?? string.Empty, ct).ConfigureAwait(false);

                    definition.Headers.Add(new HeaderValue(key, $"{{{{{key}}}}}", Enabled(header)));
                    notes.Add($"The {key} header on '{name}' looked like a credential and went to the credential store.");
                    continue;
                }

                definition.Headers.Add(new HeaderValue(key, value ?? string.Empty, Enabled(header)));
            }
        }

        if (request.TryGetProperty("body", out var body))
        {
            definition.Body = ReadBody(body, name, notes);
        }

        if (request.TryGetProperty("auth", out var auth))
        {
            var kind = Text(auth, "type") ?? "unknown";
            notes.Add($"'{name}' used {kind} auth. Point it at an auth profile; the secret was not copied into the file.");
        }

        return definition;
    }

    /// <summary>
    /// Postman stores a URL either as a string or as an exploded object. The object form carries
    /// the query separately, which is the shape Courier wants anyway.
    /// </summary>
    private static string ReadUrl(JsonElement request, RequestDefinition definition)
    {
        if (!request.TryGetProperty("url", out var url))
        {
            return string.Empty;
        }

        if (url.ValueKind == JsonValueKind.String)
        {
            return url.GetString() ?? string.Empty;
        }

        if (url.TryGetProperty("query", out var query))
        {
            foreach (var parameter in query.EnumerateArray())
            {
                var key = Text(parameter, "key");
                if (key is not null)
                {
                    definition.Query.Add(new QueryParameter(
                        key,
                        Text(parameter, "value") ?? string.Empty,
                        Enabled(parameter),
                        Text(parameter, "description")));
                }
            }
        }

        if (url.TryGetProperty("variable", out var pathVariables))
        {
            foreach (var variable in pathVariables.EnumerateArray())
            {
                var key = Text(variable, "key");
                if (key is not null)
                {
                    definition.PathParams[key] = Text(variable, "value") ?? string.Empty;
                }
            }
        }

        // "raw" holds the whole URL including the query, which would duplicate what was just read.
        var raw = Text(url, "raw");
        if (raw is not null)
        {
            var questionMark = raw.IndexOf('?');
            return questionMark >= 0 ? raw[..questionMark] : raw;
        }

        return string.Empty;
    }

    private static RequestBody? ReadBody(JsonElement body, string requestName, List<string> notes)
    {
        var mode = Text(body, "mode");

        switch (mode)
        {
            case "raw":
                var language = body.TryGetProperty("options", out var options)
                    && options.TryGetProperty("raw", out var rawOptions)
                        ? Text(rawOptions, "language")
                        : null;

                return new RequestBody
                {
                    Kind = language switch
                    {
                        "json" => BodyKind.Json,
                        "xml" => BodyKind.Xml,
                        "graphql" => BodyKind.GraphQl,
                        _ => BodyKind.Text,
                    },
                    Text = Text(body, "raw"),
                };

            case "urlencoded":
            case "formdata":
                var form = new RequestBody
                {
                    Kind = mode == "urlencoded" ? BodyKind.FormUrlEncoded : BodyKind.Multipart,
                };

                if (body.TryGetProperty(mode, out var fields))
                {
                    foreach (var field in fields.EnumerateArray())
                    {
                        var key = Text(field, "key");
                        if (key is not null)
                        {
                            form.Form.Add(new FormField(
                                key,
                                Text(field, "value"),
                                Text(field, "src"),
                                Text(field, "contentType"),
                                Enabled(field)));
                        }
                    }
                }

                return form;

            case "graphql":
                return new RequestBody
                {
                    Kind = BodyKind.GraphQl,
                    Text = body.TryGetProperty("graphql", out var graphql) ? Text(graphql, "query") : null,
                    GraphQlVariables = body.TryGetProperty("graphql", out var g) ? Text(g, "variables") : null,
                };

            case "file":
                notes.Add($"'{requestName}' had a file body. Re-select the file; Courier does not copy it into the collection.");
                return new RequestBody { Kind = BodyKind.Binary };

            default:
                return null;
        }
    }

    private static void ReadScripts(JsonElement events, RequestDefinition request)
    {
        string? pre = null, post = null;

        foreach (var element in events.EnumerateArray())
        {
            if (!element.TryGetProperty("script", out var script)
                || !script.TryGetProperty("exec", out var exec))
            {
                continue;
            }

            var lines = exec.ValueKind == JsonValueKind.Array
                ? string.Join('\n', exec.EnumerateArray().Select(l => l.GetString()))
                : exec.GetString();

            switch (Text(element, "listen"))
            {
                case "prerequest":
                    pre = lines;
                    break;

                case "test":
                    post = lines;
                    break;
            }
        }

        if (pre is not null || post is not null)
        {
            request.Scripts = new RequestScripts(pre, post);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Enabled(JsonElement element) =>
        !element.TryGetProperty("disabled", out var disabled) || !disabled.GetBoolean();
}

/// <param name="Notes">
/// What did not carry over, by name. An import that quietly drops half a collection is worse than
/// one that refuses, because the user finds out weeks later.
/// </param>
public sealed record ImportResult(
    CollectionDefinition Collection,
    IReadOnlyList<RequestDefinition> Requests,
    IReadOnlyList<EnvironmentDefinition> Environments,
    IReadOnlyList<string> Notes);
