using System.Text;
using Courier.Core.Collections;

namespace Courier.Core.Export;

/// <summary>
/// Exports a request as curl, as an <c>.http</c> file, or as client code. CORE-10.
/// </summary>
/// <remarks>
/// Every one of these is an exit route, and that is deliberate. STOR-02 wants the collection to
/// stay useful without this tool; being able to hand someone a curl line or a C# client without
/// asking them to install anything is the same principle applied to a single request.
///
/// No exporter ever emits a resolved secret. Variables stay as <c>{{name}}</c> references, which is
/// both safer and more useful — the recipient binds their own.
/// </remarks>
public static class RequestExporters
{
    /// <summary>A curl command, wrapped across lines the way a person would write it.</summary>
    public static string ToCurl(RequestDefinition request, bool windowsLineContinuation = false)
    {
        var continuation = windowsLineContinuation ? " ^" : " \\";
        var sb = new StringBuilder();

        sb.Append("curl --request ").Append(request.Method).Append(continuation).AppendLine();
        sb.Append("  --url '").Append(BuildUrl(request)).Append('\'');

        foreach (var header in request.Headers.Where(h => h.Enabled))
        {
            sb.Append(continuation).AppendLine();
            sb.Append("  --header '").Append(header.Name).Append(": ").Append(header.Value).Append('\'');
        }

        if (request.Body is { } body)
        {
            switch (body.Kind)
            {
                case BodyKind.Json or BodyKind.Xml or BodyKind.Text or BodyKind.GraphQl:
                    if (!string.IsNullOrWhiteSpace(body.Text))
                    {
                        sb.Append(continuation).AppendLine();
                        sb.Append("  --data '").Append(body.Text.Replace("'", @"'\''")).Append('\'');
                    }

                    break;

                case BodyKind.FormUrlEncoded:
                    foreach (var field in body.Form.Where(f => f.Enabled))
                    {
                        sb.Append(continuation).AppendLine();
                        sb.Append("  --data-urlencode '").Append(field.Name).Append('=').Append(field.Value).Append('\'');
                    }

                    break;

                case BodyKind.Multipart:
                    foreach (var field in body.Form.Where(f => f.Enabled))
                    {
                        sb.Append(continuation).AppendLine();
                        sb.Append("  --form '").Append(field.Name).Append('=');
                        sb.Append(field.FilePath is not null ? $"@{field.FilePath}" : field.Value).Append('\'');
                    }

                    break;
            }
        }

        // Deliberately never emits --insecure. ENT-11 has no global switch, and exporting one
        // would hand someone else a command that silently skips validation.
        return sb.ToString();
    }

    /// <summary>
    /// The <c>.http</c> format VS Code and Rider both run. The most useful export there is,
    /// because the recipient needs nothing installed to read it and almost nothing to run it.
    /// </summary>
    public static string ToHttpFile(RequestDefinition request, CollectionDefinition? collection = null)
    {
        var sb = new StringBuilder();

        if (collection is { Variables.Count: > 0 })
        {
            foreach (var (name, value) in collection.Variables)
            {
                sb.Append('@').Append(name).Append(" = ").AppendLine(value);
            }

            sb.AppendLine();
        }

        if (request.Description is { Length: > 0 } description)
        {
            foreach (var line in description.Split('\n'))
            {
                sb.Append("# ").AppendLine(line.Trim());
            }
        }

        sb.Append("### ").AppendLine(request.Name);
        sb.Append(request.Method).Append(' ').AppendLine(BuildUrl(request));

        foreach (var header in request.Headers.Where(h => h.Enabled))
        {
            sb.Append(header.Name).Append(": ").AppendLine(header.Value);
        }

        if (request.Body?.Text is { Length: > 0 } body)
        {
            if (request.Body.ContentType is { Length: > 0 } contentType
                && !request.Headers.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append("Content-Type: ").AppendLine(contentType);
            }

            // The blank line is the format's body separator, and omitting it is the single most
            // common way a hand-written .http file fails to run.
            sb.AppendLine();
            sb.AppendLine(body);
        }

        return sb.ToString();
    }

    /// <summary>A C# client using HttpClient. CORE-10.</summary>
    public static string ToCSharp(RequestDefinition request)
    {
        var sb = new StringBuilder();

        sb.AppendLine("using System.Net.Http.Headers;");
        sb.AppendLine();
        sb.AppendLine("using var client = new HttpClient();");
        sb.AppendLine();
        sb.Append("using var request = new HttpRequestMessage(HttpMethod.")
          .Append(MethodName(request.Method))
          .Append(", \"").Append(BuildUrl(request)).AppendLine("\");");

        foreach (var header in request.Headers.Where(h => h.Enabled))
        {
            sb.Append("request.Headers.TryAddWithoutValidation(\"")
              .Append(header.Name).Append("\", \"").Append(Escape(header.Value)).AppendLine("\");");
        }

        if (request.Body?.Text is { Length: > 0 } body)
        {
            sb.AppendLine();
            sb.Append("request.Content = new StringContent(")
              .AppendLine("\"\"\"");
            sb.AppendLine(body);
            sb.Append("\"\"\", System.Text.Encoding.UTF8, \"")
              .Append(request.Body.ResolveContentType()).AppendLine("\");");
        }

        sb.AppendLine();
        sb.AppendLine("using var response = await client.SendAsync(request);");
        sb.AppendLine("Console.WriteLine((int)response.StatusCode);");
        sb.AppendLine("Console.WriteLine(await response.Content.ReadAsStringAsync());");

        return sb.ToString();
    }

    /// <summary>A Python client using requests. CORE-10.</summary>
    public static string ToPython(RequestDefinition request)
    {
        var sb = new StringBuilder();

        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.Append("url = \"").Append(BuildUrl(request)).AppendLine("\"");

        var headers = request.Headers.Where(h => h.Enabled).ToList();
        if (headers.Count > 0)
        {
            sb.AppendLine("headers = {");
            foreach (var header in headers)
            {
                sb.Append("    \"").Append(header.Name).Append("\": \"").Append(Escape(header.Value)).AppendLine("\",");
            }

            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("headers = {}");
        }

        if (request.Body?.Text is { Length: > 0 } body)
        {
            sb.AppendLine();
            sb.AppendLine("payload = \"\"\"");
            sb.AppendLine(body);
            sb.AppendLine("\"\"\"");
            sb.AppendLine();
            sb.Append("response = requests.request(\"").Append(request.Method)
              .AppendLine("\", url, headers=headers, data=payload)");
        }
        else
        {
            sb.AppendLine();
            sb.Append("response = requests.request(\"").Append(request.Method)
              .AppendLine("\", url, headers=headers)");
        }

        sb.AppendLine();
        sb.AppendLine("print(response.status_code)");
        sb.AppendLine("print(response.text)");

        return sb.ToString();
    }

    private static string BuildUrl(RequestDefinition request)
    {
        var url = request.Url;

        foreach (var (name, value) in request.PathParams)
        {
            // An unfilled route parameter keeps its token. Substituting an empty string would
            // produce /orders//cancel, which looks like a bug in the export rather than a value
            // the recipient still has to supply.
            if (value.Length > 0)
            {
                url = url.Replace($"{{{name}}}", value, StringComparison.Ordinal);
            }
        }

        var query = request.Query.Where(q => q.Enabled).ToList();
        if (query.Count == 0)
        {
            return url;
        }

        var separator = url.Contains('?') ? '&' : '?';
        return url + separator + string.Join('&', query.Select(q => $"{q.Name}={q.Value}"));
    }

    private static string MethodName(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "Get",
        "POST" => "Post",
        "PUT" => "Put",
        "PATCH" => "Patch",
        "DELETE" => "Delete",
        "HEAD" => "Head",
        "OPTIONS" => "Options",
        _ => $"Get /* {method} */",
    };

    private static string Escape(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
