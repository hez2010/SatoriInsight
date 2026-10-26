using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SatoriInsight.ViewModels;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public bool IsUpdating { get; private set; }

    public bool ReplaceAll(IReadOnlyList<T> items)
    {
        if (Count == items.Count && this.SequenceEqual(items))
        {
            return false;
        }

        CheckReentrancy();
        var countChanged = Count != items.Count;
        IsUpdating = true;

        try
        {
            // Mutating the backing list bypasses per-item notifications and avoids
            // repeatedly shifting existing rows as new events arrive at the front.
            Items.Clear();

            foreach (var item in items)
            {
                Items.Add(item);
            }

            if (countChanged)
            {
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            }

            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            // DataGridCollectionView expands range changes into per-row events.
            // A single Reset keeps the grid refresh batched as well.
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        finally
        {
            IsUpdating = false;
        }

        return true;
    }
}
