using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Courier.App.Services;

namespace Courier.App.UiTests;

/// <summary>
/// <see cref="ObservableListSync"/> — the fix for the Environments pickers going blank: a
/// Clear()-then-refill raises a Reset, which drops a bound selector's selection.
/// </summary>
public sealed class ObservableListSyncTests
{
    [Fact]
    public void Reconciling_to_an_identical_list_raises_no_change_at_all()
    {
        ObservableCollection<string> target = ["Local", "Staging"];
        var raised = false;
        target.CollectionChanged += (_, _) => raised = true;

        ObservableListSync.SyncTo(target, ["Local", "Staging"]);

        Assert.False(raised);
        Assert.Equal(["Local", "Staging"], target);
    }

    [Fact]
    public void Never_raises_a_reset()
    {
        ObservableCollection<string> target = ["Local"];
        var resets = 0;
        target.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        ObservableListSync.SyncTo(target, ["Staging", "Prod", "Local"]);

        Assert.Equal(0, resets);
        Assert.Equal(["Staging", "Prod", "Local"], target);
    }

    [Fact]
    public void Shrinks_by_removing_the_trailing_extra_items()
    {
        ObservableCollection<string> target = ["Local", "Staging", "Prod"];

        ObservableListSync.SyncTo(target, ["Local"]);

        Assert.Equal(["Local"], target);
    }

    [Fact]
    public void Grows_by_appending()
    {
        ObservableCollection<string> target = ["Local"];

        ObservableListSync.SyncTo(target, ["Local", "Staging"]);

        Assert.Equal(["Local", "Staging"], target);
    }
}
