#region

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>
///     An <see cref="ObservableCollection{T}" /> with a bulk <see cref="AddRange" /> that raises a
///     single <see cref="NotifyCollectionChangedAction.Reset" /> instead of one Add event per item.
///     A large library scan adds hundreds of entries in one reconcile pass; per-item events make
///     every bound consumer (filter re-application, ItemsControl container generation) run once per
///     entry — O(N²) total. One Reset = one rebuild.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Appends <paramref name="items" /> with a single Reset notification (no-op when empty).</summary>
    public void AddRange(IEnumerable<T> items)
    {
        bool any = false;
        foreach (T item in items)
        {
            Items.Add(item); // Items bypasses per-add notifications
            any = true;
        }

        if (any)
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
