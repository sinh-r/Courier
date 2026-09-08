using Courier.Core.Collections;

namespace Courier.Core.Tests;

/// <summary>
/// <see cref="QueryString"/> — the split/compose pair shared by <c>CurlImporter</c>,
/// <c>RequestExporters</c>, and the request builder's URL ↔ Params sync.
/// </summary>
public sealed class QueryStringTests
{
    [Fact]
    public void Split_separates_the_query_string_and_decodes_each_pair()
    {
        var (url, parameters) = QueryString.Split("https://api.example.com/orders?status=open&q=a%20b");

        Assert.Equal("https://api.example.com/orders", url);
        Assert.Equal(2, parameters.Count);
        Assert.Equal("status", parameters[0].Name);
        Assert.Equal("open", parameters[0].Value);
        Assert.Equal("a b", parameters[1].Value);
    }

    [Fact]
    public void Split_on_a_url_with_no_query_returns_it_unchanged()
    {
        var (url, parameters) = QueryString.Split("https://api.example.com/orders");

        Assert.Equal("https://api.example.com/orders", url);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Compose_appends_only_enabled_named_parameters()
    {
        var url = QueryString.Compose(
            "https://api.example.com/orders",
            [
                new QueryParameter("page", "1", Enabled: true),
                new QueryParameter("status", "open", Enabled: false),
                new QueryParameter(string.Empty, "ignored", Enabled: true),
            ]);

        Assert.Equal("https://api.example.com/orders?page=1", url);
    }

    [Fact]
    public void Compose_does_not_percent_encode_a_variable_reference()
    {
        // CORE-04: the URL bar must keep {{name}} legible. Encoding is RequestPreparer's job, once,
        // at send time — never here.
        var url = QueryString.Compose("{{baseUrl}}/orders", [new QueryParameter("token", "{{apiKey}}")]);

        Assert.Equal("{{baseUrl}}/orders?token={{apiKey}}", url);
    }

    [Fact]
    public void Compose_with_no_enabled_parameters_returns_the_url_unchanged()
    {
        var url = QueryString.Compose("https://api.example.com/orders", [new QueryParameter("page", "1", Enabled: false)]);

        Assert.Equal("https://api.example.com/orders", url);
    }

    [Fact]
    public void Split_then_compose_round_trips()
    {
        const string original = "https://api.example.com/orders?status=open&page=2";
        var (url, parameters) = QueryString.Split(original);

        Assert.Equal(original, QueryString.Compose(url, parameters));
    }
}
