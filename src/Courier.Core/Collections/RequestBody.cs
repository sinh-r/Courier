namespace Courier.Core.Collections;

/// <summary>
/// A request body in one of the forms CORE-02 requires. The kind drives the editor, the syntax
/// highlighting and the content type.
/// </summary>
public sealed class RequestBody
{
    public BodyKind Kind { get; set; } = BodyKind.None;

    /// <summary>Text payload for JSON, XML, GraphQL and raw. Null for form and binary bodies.</summary>
    public string? Text { get; set; }

    /// <summary>Overrides the content type the kind would otherwise imply.</summary>
    public string? ContentType { get; set; }

    /// <summary>Fields for form-urlencoded and multipart bodies.</summary>
    public List<FormField> Form { get; set; } = [];

    /// <summary>Path to the file for a binary body. The file itself is never copied into the collection.</summary>
    public string? BinaryPath { get; set; }

    /// <summary>GraphQL variables, held separately from the query so both can be edited as JSON.</summary>
    public string? GraphQlVariables { get; set; }

    public string ResolveContentType() => ContentType ?? Kind switch
    {
        BodyKind.Json => "application/json",
        BodyKind.Xml => "application/xml",
        BodyKind.GraphQl => "application/json",
        BodyKind.FormUrlEncoded => "application/x-www-form-urlencoded",
        BodyKind.Multipart => "multipart/form-data",
        BodyKind.Binary => "application/octet-stream",
        BodyKind.Text => "text/plain",
        _ => string.Empty,
    };

    public RequestBody Clone() => new()
    {
        Kind = Kind,
        Text = Text,
        ContentType = ContentType,
        Form = [.. Form.Select(f => f with { })],
        BinaryPath = BinaryPath,
        GraphQlVariables = GraphQlVariables,
    };
}

public enum BodyKind
{
    None,
    Json,
    Xml,
    FormUrlEncoded,
    Multipart,
    GraphQl,
    Binary,
    Text,
}

/// <param name="FilePath">Set for a multipart file part; the content is read at send time.</param>
public sealed record FormField(
    string Name,
    string? Value = null,
    string? FilePath = null,
    string? ContentType = null,
    bool Enabled = true)
{
    /// <summary>For the deserializer. See <see cref="Collections.QueryParameter"/>.</summary>
    public FormField()
        : this(string.Empty)
    {
    }
}
