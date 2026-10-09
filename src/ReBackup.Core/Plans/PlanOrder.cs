namespace ReBackup.Core.Plans;

/// <summary>The order of the plan list: as saved in the settings, with plans the saved order does not name at the end.</summary>
public static class PlanOrder
{
    /// <summary>
    /// The plans in the order of <paramref name="order"/> (plan ids, case-insensitive, the first mention counts); the
    /// plans it does not name (new, or from another PC) follow, sorted by name. Ids of deleted plans are ignored.
    /// </summary>
    public static IReadOnlyList<BackupPlan> Apply(IEnumerable<BackupPlan> plans, IReadOnlyList<string>? order) =>
        Apply(plans, plan => plan.Id, plan => plan.Name, order);

    /// <summary>
    /// Where a plan that appeared (e.g. synced from another PC) goes in a list kept in <paramref name="order"/>: before
    /// the first item the order puts after it, or at the end (also when the order does not name it). Items the order
    /// does not name count as last.
    /// </summary>
    public static int InsertionIndex<T>(IReadOnlyList<T> current, Func<T, string> id, string newId, IReadOnlyList<string>? order)
    {
        var rank = Ranks(order);
        if (!rank.TryGetValue(newId, out var newRank))
            return current.Count;
        for (var i = 0; i < current.Count; i++)
        {
            if (!rank.TryGetValue(id(current[i]), out var itemRank) || itemRank > newRank)
                return i;
        }
        return current.Count;
    }

    /// <inheritdoc cref="Apply(IEnumerable{BackupPlan}, IReadOnlyList{string}?)"/>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> items, Func<T, string> id, Func<T, string> name,
        IReadOnlyList<string>? order)
    {
        var rank = Ranks(order);
        return items
            .OrderBy(item => rank.TryGetValue(id(item), out var position) ? position : int.MaxValue)
            .ThenBy(name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The position of each id in the order; the first mention counts.</summary>
    private static Dictionary<string, int> Ranks(IReadOnlyList<string>? order)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (order is not null)
        {
            foreach (var planId in order)
            {
                if (planId is not null)
                    rank.TryAdd(planId, rank.Count);
            }
        }
        return rank;
    }
}
