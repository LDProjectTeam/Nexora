using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Vellum.App.ViewModels
{
    /// <summary>Observable collection that can publish large result sets in one UI update.</summary>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> items)
        {
            CheckReentrancy();
            var changed = false;
            foreach (var item in items)
            {
                Items.Add(item);
                changed = true;
            }

            if (!changed)
                return;

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void AppendRange(IEnumerable<T> items)
        {
            foreach (var item in items)
                Add(item);
        }

        /// <summary>
        /// Brings the collection in line with <paramref name="target"/> without raising a
        /// destructive Reset when the new list only extends (or trims) the current one.
        /// A Reset forces WPF to throw away every item container, which can leave
        /// freshly generated cards without a DataContext or an ancestor binding.
        /// </summary>
        public void SyncTo(IReadOnlyList<T> target)
        {
            CheckReentrancy();
            var comparer = EqualityComparer<T>.Default;
            var shared = Math.Min(Count, target.Count);
            var isPrefix = true;
            for (var index = 0; index < shared; index++)
            {
                if (!comparer.Equals(Items[index], target[index]))
                {
                    isPrefix = false;
                    break;
                }
            }

            if (isPrefix)
            {
                if (Count == target.Count)
                    return;

                if (Count < target.Count)
                {
                    for (var index = Count; index < target.Count; index++)
                        Add(target[index]);
                }
                else
                {
                    while (Count > target.Count)
                        RemoveAt(Count - 1);
                }

                return;
            }

            ReplaceAll(target);
        }

        public void ReplaceAll(IEnumerable<T> items)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
