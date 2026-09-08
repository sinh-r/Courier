using System.Text.Json;

namespace Courier.Scanner.Tests;

/// <summary>
/// SCAN-04: a same-namespace dotted body reference (<c>Create.Command</c>) has to resolve against
/// the type <em>that namespace</em> actually declares, not whichever same-named nested type the
/// scanner happened to parse last. This is the ordinary shape of a MediatR/CQRS vertical slice —
/// every feature nesting its own <c>Command</c>, often inside an identically-named wrapper class
/// too — and it is exactly what broke on the real-world reproduction (Conduit's RealWorld reference
/// app, github.com/gothinkster/aspnetcore-realworld-example-app): <c>Articles/Create.cs</c> and
/// <c>Users/Create.cs</c> both declare <c>Create.Command</c>, and a bare "last writer wins" index
/// resolved <c>ArticlesController</c>'s body to <c>Users</c>' shape.
/// </summary>
public sealed class NamespaceCollisionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("courier-ns-collision-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_dotted_body_reference_resolves_to_its_own_namespaces_type_not_a_same_named_one_elsewhere()
    {
        // Two feature slices, each with its own Create.cs declaring a nested Create.Command — same
        // simple name, same nesting shape, different members, different namespace. Processed in
        // this order (Widgets before Zebras) so a bare "last writer wins" index would pick
        // Zebras' Command for Widgets' own controller if the fix regressed.
        WriteFile("Widgets/Create.cs", """
            using MediatR;

            namespace Demo.Features.Widgets;

            public class Create
            {
                public class WidgetData
                {
                    public string? Sku { get; init; }
                }

                public record Command(WidgetData Widget) : IRequest<int>;
            }
            """);

        WriteFile("Zebras/Create.cs", """
            using MediatR;

            namespace Demo.Features.Zebras;

            public class Create
            {
                public class ZebraData
                {
                    public string? Model { get; init; }
                }

                public record Command(ZebraData Zebra) : IRequest<int>;
            }
            """);

        WriteFile("Widgets/WidgetsController.cs", """
            using Microsoft.AspNetCore.Mvc;

            namespace Demo.Features.Widgets;

            [Route("widgets")]
            public class WidgetsController : Controller
            {
                [HttpPost]
                public IActionResult Create([FromBody] Create.Command command) => Ok();
            }
            """);

        var result = new SolutionScanner().Scan(_root, ct: TestContext.Current.CancellationToken);
        var create = result.Endpoints.Single(e => e.DeclaringType.EndsWith("WidgetsController", StringComparison.Ordinal));

        Assert.NotNull(create.SampleBody);
        Assert.Empty(create.PartialResolutionNotes);

        using var document = JsonDocument.Parse(create.SampleBody);
        var widget = document.RootElement.GetProperty("widget");

        // Widgets' own shape (sku), not Zebras' (model) — the actual bug: this used to come back
        // either null (unresolvable) or, worse, silently shaped like the wrong feature entirely.
        Assert.True(widget.TryGetProperty("sku", out _));
        Assert.False(widget.TryGetProperty("model", out _));
    }

    private void WriteFile(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
