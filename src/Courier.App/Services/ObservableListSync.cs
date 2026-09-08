using System.Collections.ObjectModel;

namespace Courier.App.Services;

/// <summary>
/// Reconciles an <see cref="ObservableCollection{T}"/> to a desired sequence in place, rather than
/// <c>Clear()</c>-then-refill. A <c>Clear()</c> raises a <c>Reset</c>, which makes a bound
/// <c>ComboBox</c>/<c>ListBox</c> drop its selection and, over a two-way <c>SelectedItem</c> binding,
/// write <c>null</c> straight back into the view model — see the environment pickers this exists to
/// fix. Reconciling by index instead means a refresh that lands on the same list (the common case)
/// touches the collection not at all, and selection survives.
/// </summary>
public static class ObservableListSync
{
    public static void SyncTo<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        var comparer = EqualityComparer<T>.Default;

        var i = 0;
        for (; i < desired.Count; i++)
        {
            if (i < target.Count)
            {
                if (!comparer.Equals(target[i], desired[i]))
                {
                    target[i] = desired[i];
                }
            }
            else
            {
                target.Add(desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
