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

    /// <inheritdoc cref="Apply(IEnumerable{BackupPlan}, IReadOnlyList{string}?)"/>
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> items, Func<T, string> id, Func<T, string> name,
        IReadOnlyList<string>? order)
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

        return items
            .OrderBy(item => rank.TryGetValue(id(item), out var position) ? position : int.MaxValue)
            .ThenBy(name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
