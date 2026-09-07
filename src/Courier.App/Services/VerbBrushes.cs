using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Courier.App.Services;

/// <summary>
/// The verb and status colour lookups. UI_SPEC 3.3: colour is data.
/// </summary>
/// <remarks>
/// Resolved from the theme dictionary rather than hard-coded so the dark mirror stays correct, and
/// cached per theme variant because a 100-tab strip asks for these on every layout pass.
///
/// <b>Never rely on colour alone.</b> Every one of these is paired with a glyph or a text label at
/// the call site — the security reviewer may well be colour-blind, and so may the engineer.
/// </remarks>
public static class VerbBrushes
{
    private static readonly Dictionary<(ThemeVariant, string), IBrush> Cache = [];

    public static IBrush For(string method)
    {
        var key = method.ToUpperInvariant() switch
        {
            "GET" => "VerbGet",
            "POST" => "VerbPost",
            "PUT" => "VerbPut",
            "PATCH" => "VerbPatch",
            "DELETE" or "DEL" => "VerbDelete",
            _ => "Muted",
        };

        return Resolve(key);
    }

    /// <summary>
    /// Status class colour. 2xx, 3xx, 4xx, 5xx — and 0, which is not a status at all but a request
    /// that never left, and gets the error colour plus a distinct glyph at the call site.
    /// </summary>
    public static IBrush ForStatus(int status) => Resolve((status / 100) switch
    {
        2 => "Ok",
        3 => "Muted",
        4 => "Warn",
        5 => "Error",
        _ => "Error",
    });

    /// <summary>Trust pair, identical everywhere: secret/local warn, shareable/committed ok.</summary>
    public static IBrush ForTrust(bool isSecret) => Resolve(isSecret ? "Secret" : "Share");

    /// <summary>The response tab strip's selected/unselected text colour.</summary>
    public static IBrush ForActiveTab(bool active) => Resolve(active ? "Ink" : "Muted");

    /// <summary>
    /// The status glyph. Paired with the colour so the distinction survives a colour-blind reader
    /// and a greyscale screenshot in a bug report.
    /// </summary>
    public static string StatusGlyph(int status, bool transportFailed) => transportFailed
        ? "⊘"                              // ⊘ — the request never left
        : (status / 100) switch
        {
            2 => "●",                      // ●
            3 => "◐",                      // ◐
            4 => "▲",                      // ▲
            5 => "■",                      // ■
            _ => "○",                      // ○
        };

    /// <summary>
    /// Provenance markers for the tree. UI_SPEC 5.1 asks for three states distinguished without
    /// noise; these are the glyphs the mock's legend uses.
    /// </summary>
    public static string ProvenanceGlyph(Core.Collections.Provenance provenance) => provenance switch
    {
        Core.Collections.Provenance.Generated => "■",        // ■ from code
        Core.Collections.Provenance.GeneratedEdited => "◧",  // ◧ edited
        _ => "○",                                            // ○ yours
    };

    public static string ProvenanceLabel(Core.Collections.Provenance provenance) => provenance switch
    {
        Core.Collections.Provenance.Generated => "from code",
        Core.Collections.Provenance.GeneratedEdited => "edited",
        _ => "yours",
    };

    private static IBrush Resolve(string key)
    {
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;

        if (Cache.TryGetValue((variant, key), out var cached))
        {
            return cached;
        }

        var brush = Application.Current?.TryGetResource(key, variant, out var value) == true && value is IBrush found
            ? found
            : Brushes.Gray;

        Cache[(variant, key)] = brush;
        return brush;
    }

    /// <summary>Called on a theme switch; the cache is keyed by variant but brushes may be replaced.</summary>
    public static void Invalidate() => Cache.Clear();
}
