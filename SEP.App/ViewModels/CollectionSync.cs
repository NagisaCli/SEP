using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SEP.App.ViewModels;

/// <summary>
/// Makes an <see cref="ObservableCollection{T}"/> equal to a wanted list with removes, moves and inserts only, so
/// items that stay keep their generated containers (no flicker, no re-templating of every card or row). Items are
/// compared by reference: a list rebuilt from the same model objects changes nothing on screen.
/// </summary>
internal static class CollectionSync
{
    /// <summary>Returns false when <paramref name="target"/> already held exactly <paramref name="wanted"/>.</summary>
    public static bool Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        if (SameSequence(target, wanted)) return false;

        IEqualityComparer<T> byReference = ReferenceEqualityComparer.Instance;
        var keep = new HashSet<T>(wanted, byReference);
        int kept = 0;
        foreach (var item in target) if (keep.Contains(item)) kept++;

        // nothing survives (a fresh listing): one Reset is far cheaper than a Remove event per row
        if (kept == 0) target.Clear();
        else
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (!keep.Contains(target[i])) target.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;
            int at = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], item)) { at = j; break; }
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, item);
        }
        while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
        return true;
    }

    private static bool SameSequence<T>(IList<T> target, IReadOnlyList<T> wanted) where T : class
    {
        if (target.Count != wanted.Count) return false;
        for (int i = 0; i < wanted.Count; i++)
            if (!ReferenceEquals(target[i], wanted[i])) return false;
        return true;
    }
}
