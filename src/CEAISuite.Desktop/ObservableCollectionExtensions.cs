using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace CEAISuite.Desktop;

/// <summary>
/// ObservableCollection that supports bulk replacement with a single Reset notification
/// instead of N individual Add notifications.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces all items, firing a single <see cref="NotifyCollectionChangedAction.Reset"/>
    /// notification instead of N+1 individual notifications.
    /// </summary>
    public void ReplaceAll(IList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
    }
}
