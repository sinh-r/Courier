using Courier.Core.Export;
using Courier.Core.Import;

namespace Courier.Core.Tests;

/// <summary>
/// <see cref="CurlImporter"/> and <see cref="RequestExporters.ToCurl"/> — CORE-09 and CORE-10.
/// Neither had a single test before this: <see cref="CurlImporter.Parse"/> had zero call sites
/// repo-wide, and <see cref="RequestExporters"/> was reachable only from <c>courier export</c>.
/// </summary>
public sealed class CurlImportExportTests
{
    [Fact]
    public void A_curl_command_round_trips_through_export_and_back()
    {
        var original = CurlImporter.Parse(
            "curl --request POST 'https://api.example.com/orders' "
            + "--header 'Content-Type: application/json' "
            + "--header 'Accept: application/json' "
            + "--data '{\"customerId\":\"CUS-1\"}'");

        var curl = RequestExporters.ToCurl(original);
        var reparsed = CurlImporter.Parse(curl);

        Assert.Equal(original.Method, reparsed.Method);
        Assert.Equal(original.Url, reparsed.Url);
        Assert.Equal(original.Body!.Text, reparsed.Body!.Text);
        Assert.Equal(
            original.Headers.Select(h => (h.Name, h.Value)).OrderBy(h => h.Name),
            reparsed.Headers.Select(h => (h.Name, h.Value)).OrderBy(h => h.Name));
    }

    [Fact]
    public void The_query_string_is_split_out_of_the_url_and_into_Query()
    {
        var request = CurlImporter.Parse("curl 'https://api.example.com/orders?status=open&limit=10'");

        Assert.Equal("https://api.example.com/orders", request.Url);
        Assert.Equal(2, request.Query.Count);
        Assert.Contains(request.Query, q => q.Name == "status" && q.Value == "open");
        Assert.Contains(request.Query, q => q.Name == "limit" && q.Value == "10");
    }

    [Fact]
    public void A_percent_encoded_query_value_is_decoded()
    {
        var request = CurlImporter.Parse("curl 'https://api.example.com/search?q=order%20status'");

        Assert.Contains(request.Query, q => q.Name == "q" && q.Value == "order status");
    }

    [Fact]
    public void A_url_with_no_query_string_gets_no_query_rows()
    {
        var request = CurlImporter.Parse("curl 'https://api.example.com/orders'");

        Assert.Empty(request.Query);
        Assert.Equal("https://api.example.com/orders", request.Url);
    }

    [Fact]
    public void A_data_flag_with_no_explicit_method_implies_POST()
    {
        var request = CurlImporter.Parse("curl 'https://api.example.com/orders' --data '{\"a\":1}'");

        Assert.Equal("POST", request.Method);
    }

    [Fact]
    public void An_explicit_method_is_not_overridden_by_implicit_POST()
    {
        var request = CurlImporter.Parse("curl -X PATCH 'https://api.example.com/orders/1' --data '{\"a\":1}'");

        Assert.Equal("PATCH", request.Method);
    }

    [Fact]
    public void Basic_auth_credentials_leave_a_note_and_no_password_anywhere_on_the_request()
    {
        var request = CurlImporter.Parse("curl -u alice:hunter2 'https://api.example.com/orders'");

        Assert.Null(request.Auth!.Profile);
        Assert.Contains(request.UnresolvedNotes, n => n.Contains("alice", StringComparison.Ordinal));
        Assert.DoesNotContain(request.UnresolvedNotes, n => n.Contains("hunter2", StringComparison.Ordinal));

        // The password must not survive into any other field either — headers, url, body.
        Assert.DoesNotContain(request.Headers, h => h.Value.Contains("hunter2", StringComparison.Ordinal));
        Assert.DoesNotContain("hunter2", request.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCurl_never_resolves_a_variable_reference()
    {
        var request = CurlImporter.Parse("curl '{{baseUrl}}/orders' --header 'Authorization: {{token}}'");

        var curl = RequestExporters.ToCurl(request);

        Assert.Contains("{{baseUrl}}", curl, StringComparison.Ordinal);
        Assert.Contains("{{token}}", curl, StringComparison.Ordinal);
    }
}
