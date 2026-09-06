using Microsoft.AspNetCore.Mvc;

namespace Legacy.Api.Controllers;

/// <summary>
/// The long tail. REQUIREMENTS 9: "every codebase has a pattern that breaks it. Fail visibly per
/// endpoint, never silently."
/// </summary>
/// <remarks>
/// Every action here is something a real scanner trips over. The test asserts that each one either
/// resolves correctly or appears in the unresolved list with a reason — what must never happen is
/// that one of them silently disappears.
/// </remarks>
[Route("legacy/[controller]")]
public class AwkwardController : Controller
{
    /// <summary>A route built from a constant in another file. The syntax tier cannot follow it.</summary>
    [HttpGet(RouteConstants.SearchPath)]
    public IActionResult Search(string q) => Ok();

    /// <summary>An absolute template, which replaces the controller prefix rather than extending it.</summary>
    [HttpGet("/legacy/absolute/{id:int}")]
    public IActionResult Absolute(int id) => Ok();

    /// <summary>Two verbs on one action, which must produce two endpoints.</summary>
    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    public IActionResult Upsert(string id, [FromBody] object body) => Ok();

    /// <summary>Conventional routing only: no template anywhere, so the URL is not derivable.</summary>
    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>A catch-all route parameter.</summary>
    [HttpGet("files/{**path}")]
    public IActionResult Files(string path) => Ok();

    /// <summary>Constraints, defaults and optionals that must all normalise to the same identity.</summary>
    [HttpDelete("items/{id:guid}/{soft:bool=true}/{note?}")]
    public IActionResult Remove(Guid id, bool soft, string? note) => Ok();
}

internal static class RouteConstants
{
    public const string SearchPath = "search";
}
