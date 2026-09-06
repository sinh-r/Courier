using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Courier.App.ViewModels;

namespace Courier.App.Controls;

/// <summary>
/// Hosts the virtualized row source and translates clicks into expansion-bitset flips.
/// </summary>
public sealed partial class JsonTreeView : UserControl
{
    private JsonRowSource? _source;

    public JsonTreeView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Rebind();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        LoadAllButton.Click += (_, _) =>
        {
            Response?.LoadEverything();
            Rebind();
        };
    }

    private ResponseViewModel? Response => DataContext as ResponseViewModel;

    private void Rebind()
    {
        if (Response is not { Index: not null, Tree: not null } response)
        {
            Rows.ItemsSource = null;
            _source = null;
            return;
        }

        _source = new JsonRowSource(response, response.Tree);
        Rows.ItemsSource = _source;
    }

    /// <summary>
    /// A click on a container row toggles it. Expansion is a bit flip plus one linear projection
    /// pass, which is why a 118,402-node document expands without a visible pause.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Response is not { Tree: { } tree } response
            || e.Source is not Visual visual
            || visual.DataContext is not JsonRow row)
        {
            return;
        }

        tree.Toggle(row.NodeIndex);
        _source?.Invalidate();

        Breadcrumb.Text = response.BreadcrumbFor(row.NodeIndex);
    }

    /// <summary>Reveals a search hit, expanding whatever collapsed branch contains it.</summary>
    public void ScrollToRow(int row)
    {
        if (row < 0)
        {
            return;
        }

        _source?.Invalidate();

        // 19px rows, matching the template, so the offset is arithmetic rather than a measure pass.
        Scroller.Offset = Scroller.Offset.WithY(Math.Max(0, (row * 19) - (Scroller.Viewport.Height / 2)));
    }
}
