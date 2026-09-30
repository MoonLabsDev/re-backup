using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ReBackup.App.ViewModels;

/// <summary>ObservableCollection that can change many items with a single Reset notification.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private List<T> List => (List<T>)Items;

    public void ReplaceAll(IEnumerable<T> items)
    {
        List.Clear();
        List.AddRange(items);
        RaiseReset();
    }

    public void InsertRange(int index, IReadOnlyCollection<T> items)
    {
        if (items.Count == 0)
            return;
        List.InsertRange(index, items);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count == 0)
            return;
        List.RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
