using System.Text;
using Courier.Core.Collections;

namespace Courier.Core.Import;

/// <summary>
/// Parses a pasted curl command. CORE-09.
/// </summary>
/// <remarks>
/// The highest-value import path by a wide margin: "send me your curl" is how every API problem
/// currently travels between two engineers, and pasting one into the URL bar is the fastest route
/// from a Slack message to a running request. It is also the path capsules are meant to replace,
/// which is why both exist.
/// </remarks>
public static class CurlImporter
{
    public static bool LooksLikeCurl(string text) =>
        text.TrimStart().StartsWith("curl", StringComparison.OrdinalIgnoreCase);

    public static RequestDefinition Parse(string command)
    {
        var tokens = Tokenize(command);
        var request = new RequestDefinition { Name = "Pasted curl", Method = "GET" };

        var bodyParts = new List<string>();
        var formParts = new List<FormField>();
        var explicitMethod = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            switch (token)
            {
                case "curl":
                    continue;

                case "-X" or "--request" when i + 1 < tokens.Count:
                    request.Method = tokens[++i].ToUpperInvariant();
                    explicitMethod = true;
                    break;

                case "-H" or "--header" when i + 1 < tokens.Count:
                    AddHeader(request, tokens[++i]);
                    break;

                case "-d" or "--data" or "--data-raw" or "--data-binary" or "--data-ascii" when i + 1 < tokens.Count:
                    bodyParts.Add(tokens[++i]);
                    break;

                case "--data-urlencode" when i + 1 < tokens.Count:
                    bodyParts.Add(tokens[++i]);
                    request.Body ??= new RequestBody { Kind = BodyKind.FormUrlEncoded };
                    break;

                case "-F" or "--form" when i + 1 < tokens.Count:
                    formParts.Add(ParseFormField(tokens[++i]));
                    break;

                case "-u" or "--user" when i + 1 < tokens.Count:
                    // Credentials on a curl line are a secret. They are not written into the
                    // request; the user points it at an auth profile instead. P2.
                    var user = tokens[++i].Split(':', 2)[0];
                    request.Auth = new AuthReference(null);
                    request.UnresolvedNotes.Add(
                        $"The command carried basic credentials for '{user}'. Create an auth profile; "
                        + "the password was not copied into this request.");
                    break;

                case "-b" or "--cookie" when i + 1 < tokens.Count:
                    request.Headers.Add(new HeaderValue("Cookie", tokens[++i]));
                    break;

                case "-A" or "--user-agent" when i + 1 < tokens.Count:
                    request.Headers.Add(new HeaderValue("User-Agent", tokens[++i]));
                    break;

                case "-L" or "--location":
                    request.Settings = request.Settings with { FollowRedirects = true };
                    break;

                case "-k" or "--insecure":
                    // ENT-11: there is no global "disable verification" in Courier, so this is
                    // recorded as something the user must decide per host rather than honoured.
                    request.UnresolvedNotes.Add(
                        "The command used --insecure. Courier does not disable certificate validation; "
                        + "if this host needs an exception, add it in Trust and network.");
                    break;

                case "--compressed" or "-s" or "--silent" or "-v" or "--verbose" or "-i" or "--include":
                    // Output and transport flags with no request-shape meaning.
                    break;

                case "-m" or "--max-time" when i + 1 < tokens.Count:
                    if (double.TryParse(tokens[++i], out var seconds))
                    {
                        request.Settings = request.Settings with { TimeoutMilliseconds = (int)(seconds * 1000) };
                    }

                    break;

                default:
                    if (!token.StartsWith('-') && request.Url.Length == 0)
                    {
                        request.Url = token;
                    }

                    break;
            }
        }

        SplitQuery(request);
        ApplyBody(request, bodyParts, formParts, explicitMethod);
        NameFromUrl(request);

        return request;
    }

    /// <summary>
    /// curl leaves the query string embedded in the URL. Splitting it into <see cref="RequestDefinition.Query"/>
    /// is what makes a pasted command show up in the Params grid instead of an opaque URL.
    /// </summary>
    private static void SplitQuery(RequestDefinition request)
    {
        var (url, parameters) = QueryString.Split(request.Url);
        request.Url = url;
        request.Query.AddRange(parameters);
    }

    private static void ApplyBody(
        RequestDefinition request,
        List<string> bodyParts,
        List<FormField> formParts,
        bool explicitMethod)
    {
        if (formParts.Count > 0)
        {
            request.Body = new RequestBody { Kind = BodyKind.Multipart, Form = formParts };
        }
        else if (bodyParts.Count > 0)
        {
            var text = string.Join('&', bodyParts);
            var declared = request.Headers
                .FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            request.Body = new RequestBody
            {
                Kind = Detect(text, declared),
                Text = text,
                ContentType = declared,
            };
        }

        // curl implies POST when there is data and no explicit method. Matching that is what makes
        // a pasted command behave the way the person who sent it expects.
        if (!explicitMethod && request.Body is not null)
        {
            request.Method = "POST";
        }
    }

    private static BodyKind Detect(string text, string? contentType)
    {
        if (contentType is not null)
        {
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return BodyKind.Json;
            }

            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return BodyKind.Xml;
            }

            if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return BodyKind.FormUrlEncoded;
            }
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? BodyKind.Json : BodyKind.Text;
    }

    private static void AddHeader(RequestDefinition request, string header)
    {
        var colon = header.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        request.Headers.Add(new HeaderValue(
            header[..colon].Trim(),
            header[(colon + 1)..].Trim()));
    }

    private static FormField ParseFormField(string field)
    {
        var equals = field.IndexOf('=');
        if (equals <= 0)
        {
            return new FormField(field);
        }

        var name = field[..equals];
        var value = field[(equals + 1)..];

        // "@path" is curl's file syntax.
        return value.StartsWith('@')
            ? new FormField(name, null, value[1..])
            : new FormField(name, value);
    }

    private static void NameFromUrl(RequestDefinition request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        request.Name = $"{request.Method} {uri.AbsolutePath}";
    }

    /// <summary>
    /// Splits a shell command, honouring single and double quotes and line continuations. Written
    /// rather than taken from a library because the input is a paste from a chat window: it has
    /// smart quotes, trailing backslashes and stray newlines, and a strict parser rejects it.
    /// </summary>
    internal static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var hasContent = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = Normalise(command[i]);

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"' && i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    hasContent = true;
                    break;

                // A line continuation, in either shell's spelling.
                case '\\' or '^' when i + 1 < command.Length && command[i + 1] is '\n' or '\r':
                    i++;
                    break;

                case ' ' or '\t' or '\n' or '\r':
                    if (hasContent || current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasContent = false;
                    }

                    break;

                default:
                    current.Append(c);
                    hasContent = true;
                    break;
            }
        }

        if (hasContent || current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Folds the smart quotes a chat client substitutes into the plain ones.</summary>
    private static char Normalise(char c) => c switch
    {
        '‘' or '’' => '\'',
        '“' or '”' => '"',
        _ => c,
    };
}
