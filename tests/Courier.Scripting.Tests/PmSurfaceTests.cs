using System.Net;
using System.Text;
using Courier.Core.Http;
using Courier.Scripting;
using Courier.Scripting.Pm;

namespace Courier.Scripting.Tests;

/// <summary>
/// The published <c>pm.*</c> surface, tested as the contract it is. TEST-02.
/// </summary>
/// <remarks>
/// REQUIREMENTS 9 treats pm.* compatibility as a risk to be bounded by a published list. These
/// tests are that list in executable form: what is here is supported and must keep working, and
/// <see cref="Reports_an_unsupported_member_by_name"/> pins the promise that everything else fails
/// loudly with the member's name rather than silently doing nothing.
/// </remarks>
public sealed class PmSurfaceTests
{
    private static ExchangeResult Response(string json, int status = 200, double ms = 184) => new()
    {
        Outcome = ExchangeOutcome.Completed,
        Request = new SentRequest(
            "POST",
            new Uri("https://qa.internal/api/v2/orders"),
            [new KeyValuePair<string, string>("Accept", "application/json")],
            "{}"u8.ToArray(),
            "application/json",
            HttpVersion.Version20),
        Response = new ReceivedResponse(
            (HttpStatusCode)status,
            status == 200 ? "OK" : "Error",
            [new KeyValuePair<string, string>("Content-Type", "application/json")],
            Encoding.UTF8.GetBytes(json),
            null,
            json.Length,
            "application/json",
            HttpVersion.Version20),
        Elapsed = TimeSpan.FromMilliseconds(ms),
        StartedUtc = DateTimeOffset.UtcNow,
    };

    private static ScriptRun RunPost(string script, ExchangeResult result, ScriptContext? context = null) =>
        new ScriptRunner().RunPostResponse(script, context ?? new ScriptContext(), result);

    [Fact]
    public void Runs_the_canonical_status_assertion_unmodified()
    {
        var run = RunPost(
            """
            pm.test("status is 200", function () {
                pm.response.to.have.status(200);
            });
            """,
            Response("""{"order":{"id":"ORD-4471"}}"""));

        Assert.True(run.Succeeded);
        Assert.Single(run.Tests);
        Assert.True(run.Tests[0].Passed);
        Assert.Equal("status is 200", run.Tests[0].Name);
    }

    [Fact]
    public void A_failing_assertion_is_a_failed_test_not_a_failed_script()
    {
        var run = RunPost(
            """
            pm.test("status is 200", function () {
                pm.response.to.have.status(200);
            });
            """,
            Response("{}", status: 500));

        // The script itself ran fine; the assertion inside it did not hold. Conflating the two
        // would show a red script error instead of a red test row.
        Assert.True(run.Succeeded);
        Assert.False(run.Tests[0].Passed);
        Assert.Contains("500", run.Tests[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_the_response_body_as_json()
    {
        var run = RunPost(
            """
            pm.test("has three lines", function () {
                var body = pm.response.json();
                pm.expect(body.order.lines).to.have.lengthOf(3);
                pm.expect(body.order.status).to.equal("Pending");
            });
            """,
            Response("""{"order":{"status":"Pending","lines":[1,2,3]}}"""));

        Assert.True(run.Tests[0].Passed, run.Tests[0].Error);
    }

    [Fact]
    public void Compares_numbers_across_json_and_literal_types()
    {
        // The mock's failing assertion is exactly this shape: body.order.total === 190.20.
        var passing = RunPost(
            """pm.test("total", function () { pm.expect(pm.response.json().total).to.equal(184.20); });""",
            Response("""{"total":184.2}"""));

        Assert.True(passing.Tests[0].Passed, passing.Tests[0].Error);

        var failing = RunPost(
            """pm.test("total", function () { pm.expect(pm.response.json().total).to.equal(190.20); });""",
            Response("""{"total":184.20}"""));

        Assert.False(failing.Tests[0].Passed);
    }

    [Fact]
    public void Reads_response_headers_and_timing()
    {
        var run = RunPost(
            """
            pm.test("json and fast", function () {
                pm.expect(pm.response.headers.get("content-type")).to.include("application/json");
                pm.expect(pm.response.responseTime).to.be.below(1000);
                pm.expect(pm.response.code).to.equal(200);
            });
            """,
            Response("{}"));

        Assert.True(run.Tests[0].Passed, run.Tests[0].Error);
    }

    [Fact]
    public void Writes_an_environment_variable_that_the_next_request_can_read()
    {
        var context = new ScriptContext();

        var run = RunPost(
            """
            var token = pm.response.json().token;
            pm.environment.set("authToken", token);
            """,
            Response("""{"token":"abc123"}"""),
            context);

        Assert.True(run.Succeeded, run.Error);

        // The whole reason scripting exists for most users: capture a value, use it next time.
        Assert.Equal("abc123", context.Environment["authToken"]);
    }

    [Fact]
    public void Reads_the_request_that_was_sent()
    {
        var run = RunPost(
            """
            pm.test("sent as post", function () {
                pm.expect(pm.request.method).to.equal("POST");
                pm.expect(pm.request.url).to.include("/api/v2/orders");
            });
            """,
            Response("{}"));

        Assert.True(run.Tests[0].Passed, run.Tests[0].Error);
    }

    [Fact]
    public void Supports_negation_and_the_grammar_words()
    {
        var run = RunPost(
            """
            pm.test("chain reads as english", function () {
                pm.expect(pm.response.json().items).to.be.an("array");
                pm.expect(pm.response.json().items).to.not.be.empty;
                pm.expect(pm.response.code).to.not.equal(500);
            });
            """,
            Response("""{"items":[1]}"""));

        Assert.True(run.Tests[0].Passed, run.Tests[0].Error);
    }

    [Fact]
    public void Reports_an_unsupported_member_by_name()
    {
        var run = RunPost("pm.sendRequest();", Response("{}"));

        // TEST-02 and REQUIREMENTS 9: named, not silent, and never claimed as supported.
        Assert.False(run.Succeeded);
        Assert.Contains("sendRequest", run.Error, StringComparison.Ordinal);
        Assert.Contains("sendRequest", run.UnsupportedCalls);
    }

    [Fact]
    public void The_postman_alias_works_for_older_scripts()
    {
        var run = RunPost(
            """postman.test("aliased", function () { postman.expect(1).to.equal(1); });""",
            Response("{}"));

        Assert.True(run.Succeeded, run.Error);
        Assert.True(run.Tests[0].Passed);
    }

    [Fact]
    public void Several_tests_in_one_script_all_report()
    {
        var run = RunPost(
            """
            pm.test("one", function () { pm.expect(1).to.equal(1); });
            pm.test("two", function () { pm.expect(1).to.equal(2); });
            pm.test("three", function () { pm.expect("a").to.be.a("string"); });
            """,
            Response("{}"));

        Assert.Equal(3, run.Tests.Count);
        Assert.Equal(2, run.Passed);
        Assert.Equal(1, run.Failed);
    }
}
