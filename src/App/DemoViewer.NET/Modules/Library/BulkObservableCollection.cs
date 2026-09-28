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
///     entry: O(N²) total. One Reset = one rebuild.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the whole contents with <paramref name="items" /> under a single Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    ///     Makes the contents equal <paramref name="target" /> with one ranged Remove per run of items that
    ///     went and one ranged Add per run that came, so items that stay keep their containers. Items are
    ///     compared by reference and must be unique. When the items that stay change order, one Reset.
    /// </summary>
    /// <param name="target">The new contents.</param>
    public void SyncTo(IReadOnlyList<T> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        HashSet<T> wanted = new(target, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        HashSet<T> present = new(Items, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        List<T> staying = [.. Items.Where(wanted.Contains)];
        if (!staying.SequenceEqual(target.Where(present.Contains), ReferenceEqualityComparer.Instance as IEqualityComparer<T>))
        {
            ReplaceAll(target);
            return;
        }

        bool changed = false;
        for (int end = Items.Count; end > 0;)
        {
            if (wanted.Contains(Items[end - 1]))
            {
                end--;
                continue;
            }

            int start = end - 1;
            while (start > 0 && !wanted.Contains(Items[start - 1]))
            {
                start--;
            }

            List<T> gone = [];
            for (int i = start; i < end; i++)
            {
                gone.Add(Items[i]);
            }

            for (int i = end - 1; i >= start; i--)
            {
                Items.RemoveAt(i);
            }

            changed = true;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, gone, start));
            end = start;
        }

        for (int i = 0; i < target.Count;)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], target[i]))
            {
                i++;
                continue;
            }

            int start = i;
            List<T> run = [];
            while (i < target.Count && !present.Contains(target[i]))
            {
                run.Add(target[i]);
                Items.Insert(i, target[i]);
                i++;
            }

            changed = true;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, run, start));
        }

        if (changed)
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        }
    }

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
