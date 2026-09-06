using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Courier.Core.Storage;

namespace Courier.App.Services;

/// <summary>
/// Dumps the arranged bounds of the shell's panes to a file.
/// </summary>
/// <remarks>
/// NFR-05 requires 100 through 200 per cent display scaling without reflow breakage, and a
/// scaling bug is invisible in a screenshot taken on the machine that has it — the capture is
/// scaled the same way the window is. This reports what the layout system actually computed, in
/// device-independent pixels, which is the only way to tell a layout defect from a display one.
///
/// Off unless COURIER_LAYOUT_DIAGNOSTICS=1. It is a developer tool, not a feature, and it writes
/// only to the app's own log directory (SEC-05 lists that location).
/// </remarks>
public static class LayoutDiagnostics
{
    public static void Dump(Window window)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"window client       {window.Bounds.Width:0.#} x {window.Bounds.Height:0.#} DIP");
        sb.AppendLine($"render scaling      {window.RenderScaling:0.###}");
        sb.AppendLine($"desktop scaling     {window.DesktopScaling:0.###}");

        if (window.Screens.ScreenFromWindow(window) is { } screen)
        {
            sb.AppendLine($"screen bounds       {screen.Bounds.Width} x {screen.Bounds.Height} px");
            sb.AppendLine($"screen working      {screen.WorkingArea.Width} x {screen.WorkingArea.Height} px");
            sb.AppendLine($"screen scaling      {screen.Scaling:0.###}");
        }

        sb.AppendLine();
        sb.AppendLine("arranged panes (DIP):");

        foreach (var visual in window.GetVisualDescendants().OfType<Control>())
        {
            var name = visual.GetType().Name;

            if (name is "CollectionTreeView" or "WorkspaceView" or "InspectorView"
                or "StatusBarView" or "TabHeaderView" or "RequestEditorView" or "ResponseView")
            {
                var origin = visual.TranslatePoint(default, window) ?? default;
                sb.AppendLine(
                    $"  {name,-20} at {origin.X,7:0.#},{origin.Y,7:0.#}  "
                    + $"{visual.Bounds.Width,7:0.#} x {visual.Bounds.Height,7:0.#}  visible={visual.IsVisible}");
            }
        }

        Directory.CreateDirectory(StorageLocations.Logs);
        File.WriteAllText(Path.Combine(StorageLocations.Logs, "layout.txt"), sb.ToString());
    }
}
