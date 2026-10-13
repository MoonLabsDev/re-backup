namespace ReBackup.Shared.Retention;

public enum CoverageQuality { Ok, Warning }

/// <summary>A version as the coverage timeline sees it. <paramref name="Counted"/> versions go through retention.</summary>
public sealed record CoverageVersion(string Id, DateTime LocalTime, CoverageQuality Quality, bool Counted, string? Detail);

public enum SlotState { Present, PresentWithWarnings, Missing, Pending, BeforeFirst }

/// <summary>One slot of a rule: <paramref name="Start"/> inclusive, <paramref name="End"/> exclusive (the next slot's start).</summary>
public sealed record CoverageSlot(DateOnly Start, DateOnly End, SlotState State, CoverageVersion? Version);

/// <summary>The slots of one rule, oldest first.</summary>
public sealed record CoverageLane(int RuleIndex, RetentionRule Rule, IReadOnlyList<CoverageSlot> Slots);

/// <summary>Which slots each retention rule wants to keep, and whether a version exists for each.</summary>
public static class RetentionCoverage
{
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static IReadOnlyList<CoverageLane> Compute(IReadOnlyList<CoverageVersion> versions,
        IReadOnlyList<RetentionRule> rules, DateOnly today)
    {
        if (rules.Count == 0)
            return [];

        var counted = new Dictionary<string, CoverageVersion>(StringComparer.Ordinal);
        foreach (var version in versions.Where(v => v.Counted))
            counted.TryAdd(version.Id, version);
        var decisions = RetentionEngine.Evaluate(
            counted.Values.Select(v => new RetentionVersion(v.Id, v.LocalTime)).ToArray(), rules);

        var oldestDate = versions.Count == 0 ? (DateOnly?)null : versions.Min(v => DateOnly.FromDateTime(v.LocalTime));
        var lanes = new List<CoverageLane>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var kept = new Dictionary<DateOnly, CoverageVersion>();
            foreach (var decision in decisions)
            {
                if (decision.Reasons.Any(r => r.RuleIndex == i))
                    kept[RetentionRules.SlotStart(rules[i], DateOnly.FromDateTime(decision.Version.LocalTime))] =
                        counted[decision.Version.Name];
            }
            lanes.Add(new CoverageLane(i, rules[i], BuildSlots(rules[i], versions, kept, oldestDate, today)));
        }
        return lanes;
    }

    private static IReadOnlyList<CoverageSlot> BuildSlots(RetentionRule rule, IReadOnlyList<CoverageVersion> versions,
        Dictionary<DateOnly, CoverageVersion> kept, DateOnly? oldestDate, DateOnly today)
    {
        var current = RetentionRules.SlotStart(rule, today);

        // Span: at least Keep calendar slots back from the current one, and back to the oldest kept version.
        var starts = new List<DateOnly> { current };
        var oldestKept = kept.Count == 0 ? current : kept.Keys.Min();
        while (starts[^1] != DateOnly.MinValue && (starts.Count < rule.Keep || starts[^1] > oldestKept))
            starts.Add(RetentionRules.SlotStart(rule, starts[^1].AddDays(-1)));
        starts.Reverse();

        var firstSlot = oldestDate is { } oldest ? RetentionRules.SlotStart(rule, oldest) : (DateOnly?)null;
        var newestNonCounted = new Dictionary<DateOnly, CoverageVersion>();
        foreach (var version in versions.Where(v => !v.Counted).OrderBy(v => v.LocalTime))
            newestNonCounted[RetentionRules.SlotStart(rule, DateOnly.FromDateTime(version.LocalTime))] = version;

        var slots = new List<CoverageSlot>(starts.Count);
        for (var s = 0; s < starts.Count; s++)
        {
            var start = starts[s];
            var end = s + 1 < starts.Count ? starts[s + 1] : NextStart(rule, start);
            if (kept.TryGetValue(start, out var version))
                slots.Add(new CoverageSlot(start, end,
                    version.Quality == CoverageQuality.Warning ? SlotState.PresentWithWarnings : SlotState.Present, version));
            else if (newestNonCounted.TryGetValue(start, out var other))
                slots.Add(new CoverageSlot(start, end, SlotState.PresentWithWarnings, other));
            else if (start == current)
                slots.Add(new CoverageSlot(start, end, SlotState.Pending, null));
            else if (firstSlot is not { } first || start < first)
                slots.Add(new CoverageSlot(start, end, SlotState.BeforeFirst, null));
            else
                slots.Add(new CoverageSlot(start, end, SlotState.Missing, null));
        }
        return slots;
    }

    private static DateOnly NextStart(RetentionRule rule, DateOnly start)
    {
        // Slots are at most a year long.
        for (var d = start.AddDays(1); d < DateOnly.MaxValue; d = d.AddDays(1))
        {
            if (RetentionRules.SlotStart(rule, d) == d)
                return d;
        }
        return DateOnly.MaxValue;
    }
}
