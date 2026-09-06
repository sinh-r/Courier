using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Courier.App.Services;
using Courier.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Courier.App.ViewModels;

/// <summary>
/// The open tabs, and the suspension policy that makes 100 of them affordable.
/// </summary>
/// <remarks>
/// <para>
/// PERF-02 and PERF-03 are called out in REQUIREMENTS 6.1 as "the differentiator ... treat them as
/// product features with named owners". This class is where they live.
/// </para>
/// <para>
/// The strip itself is cheap for a reason worth stating: UI_SPEC 4.2 fixes tabs at 180px and says
/// overflow collapses into a <c>+n</c> control rather than shrinking or scrolling. So the strip
/// only ever realizes <c>floor(width / 180)</c> headers — six or eight — whether eight tabs are
/// open or a hundred. PERF-03's 50ms switch is then about rehydrating one tab's state, not about
/// laying out a hundred headers.
/// </para>
/// </remarks>
public sealed partial class TabCollection : ObservableObject
{
    /// <summary>Tabs beyond this many, ordered by last use, are suspended.</summary>
    public const int LiveTabBudget = 8;

    private readonly Func<Task<CourierDatabase>> _database;

    [ObservableProperty]
    private TabViewModel? _active;

    public TabCollection(Func<Task<CourierDatabase>> database) => _database = database;

    public ObservableCollection<TabViewModel> Items { get; } = [];

    /// <summary>How many headers the strip can draw at the current width. Set by the view.</summary>
    public int VisibleHeaderCount { get; set; } = 6;

    /// <summary>The <c>+n</c> control's number. Zero hides it.</summary>
    public int OverflowCount => Math.Max(0, Items.Count - VisibleHeaderCount);

    /// <summary>"31 tabs" in the status bar.</summary>
    public string CountLabel => Items.Count == 1 ? "1 tab" : $"{Items.Count} tabs";

    /// <summary>Headers actually realized. The active tab is always among them.</summary>
    public IEnumerable<TabViewModel> VisibleHeaders
    {
        get
        {
            if (Items.Count <= VisibleHeaderCount)
            {
                return Items;
            }

            var visible = Items.Take(VisibleHeaderCount).ToList();

            if (Active is not null && !visible.Contains(Active))
            {
                visible[^1] = Active;
            }

            return visible;
        }
    }

    public TabViewModel Open(TabState state)
    {
        var tab = new TabViewModel(state);
        Items.Add(tab);
        Select(tab);
        return tab;
    }

    public void Close(TabViewModel tab)
    {
        var index = Items.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        tab.Response?.Release();
        Items.Remove(tab);

        if (Active == tab)
        {
            Select(Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)]);
        }

        Notify();
    }

    /// <summary>
    /// Switches tabs. Everything on this path is inside PERF-03's 50ms budget: rehydrate one
    /// serialized POCO, mark it active, and let the single content presenter rebind.
    /// </summary>
    public void Select(TabViewModel? tab)
    {
        if (ReferenceEquals(Active, tab))
        {
            return;
        }

        if (Active is not null)
        {
            Active.IsActive = false;
        }

        Active = tab;

        if (tab is not null)
        {
            tab.Activate();
            tab.IsActive = true;
        }

        EnforceBudget();
        Notify();
    }

    /// <summary>
    /// Suspends everything past the budget, least recently used first. The user is never told this
    /// happened, and must never be able to tell.
    /// </summary>
    public void EnforceBudget()
    {
        if (Items.Count <= LiveTabBudget)
        {
            return;
        }

        var live = Items
            .Where(t => !t.IsSuspended)
            .OrderByDescending(t => t.State?.LastActive ?? DateTimeOffset.MinValue)
            .ToList();

        foreach (var tab in live.Skip(LiveTabBudget))
        {
            if (!ReferenceEquals(tab, Active))
            {
                tab.Suspend();
            }
        }
    }

    /// <summary>Persists every tab so a relaunch or a crash restores them. CORE-07, NFR-07.</summary>
    public async Task SaveSessionAsync(CancellationToken ct = default)
    {
        var database = await _database().ConfigureAwait(false);

        await database.ExecuteAsync("DELETE FROM tab_state;", ct).ConfigureAwait(false);

        for (var i = 0; i < Items.Count; i++)
        {
            await using var command = database.CreateCommand(
                "INSERT INTO tab_state (tab_id, ordinal, saved_utc, payload) VALUES ($id, $ordinal, $saved, $payload);");

            command.Parameters.AddWithValue("$id", Items[i].Id);
            command.Parameters.AddWithValue("$ordinal", i);
            command.Parameters.AddWithValue("$saved", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$payload", Items[i].Serialize());

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Restores the previous session. Runs after first paint, never before. PERF-01.</summary>
    public async Task RestoreSessionAsync(CancellationToken ct = default)
    {
        CourierDatabase database;

        try
        {
            database = await _database().ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // A corrupt or locked database must not stop the app opening. The user gets an empty
            // session rather than a dialog they cannot act on.
            CrashLog.Write(ex, "session restore");
            return;
        }

        await using var command = database.CreateCommand(
            "SELECT payload FROM tab_state ORDER BY ordinal;");

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var restored = new List<TabViewModel>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tab = TabViewModel.Deserialize(reader.GetString(0));

            // Restored tabs come back suspended. Opening 60 tabs' worth of editors during startup
            // is exactly what PERF-01 forbids.
            tab.Suspend();
            restored.Add(tab);
        }

        if (restored.Count == 0)
        {
            return;
        }

        // The placeholder tab the shell opened so the panes had something to bind to is only
        // discarded once there is a real session to put in its place.
        Items.Clear();

        foreach (var tab in restored)
        {
            Items.Add(tab);
        }

        Select(Items[0]);
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(OverflowCount));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(VisibleHeaders));
    }
}
