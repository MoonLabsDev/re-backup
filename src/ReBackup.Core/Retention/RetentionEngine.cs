namespace ReBackup.Core.Retention;

/// <summary>A version as retention sees it: its folder name and the local time in that name.</summary>
public sealed record RetentionVersion(string Name, DateTime LocalTime);

/// <summary>
/// Why a version is kept. <paramref name="RuleIndex"/> is the position of the rule in the plan's list, or -1 for
/// the built-in reasons. <paramref name="Slot"/> counts the rule's slots from the most recent one (1).
/// </summary>
public sealed record KeepReason(int RuleIndex, int Slot, string Label);

public sealed record RetentionDecision(RetentionVersion Version, IReadOnlyList<KeepReason> Reasons)
{
    /// <summary>False means: retention deletes this version.</summary>
    public bool Keep => Reasons.Count > 0;
}

/// <summary>Decides which versions a plan's rules keep. Pure: it looks at names and times only.</summary>
public static class RetentionEngine
{
    public const string NewestLabel = "Newest";
    public const string NoRulesLabel = "No rules";

    /// <summary>One decision per version, oldest first.</summary>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static IReadOnlyList<RetentionDecision> Evaluate(IReadOnlyList<RetentionVersion> versions,
        IReadOnlyList<RetentionRule> rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (RetentionRules.Validate(rules[i]) is { } problem)
                throw new ArgumentException($"Retention rule {i + 1}: {problem}", nameof(rules));
        }

        var ordered = versions
            .OrderBy(v => v.LocalTime)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reasons = new List<KeepReason>[ordered.Length];
        for (var i = 0; i < reasons.Length; i++)
            reasons[i] = [];

        if (rules.Count == 0)
        {
            foreach (var list in reasons)
                list.Add(new KeepReason(-1, 0, NoRulesLabel));
        }
        else
        {
            for (var i = 0; i < rules.Count; i++)
                Claim(ordered, rules[i], i, reasons);

            if (ordered.Length > 0 && reasons[^1].Count == 0)
                reasons[^1].Add(new KeepReason(-1, 0, NewestLabel));
        }

        var decisions = new RetentionDecision[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
            decisions[i] = new RetentionDecision(ordered[i], reasons[i]);
        return decisions;
    }

    private static void Claim(RetentionVersion[] ordered, RetentionRule rule, int ruleIndex, List<KeepReason>[] reasons)
    {
        // Slot start → index of the slot's representative. The versions are visited oldest first, so versions on
        // the slot's first day come before the rest of the slot: the last of them wins; without any, the first
        // version of the slot stays.
        var representatives = new SortedDictionary<DateOnly, int>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var date = DateOnly.FromDateTime(ordered[i].LocalTime);
            var slot = RetentionRules.SlotStart(rule, date);
            if (date == slot || !representatives.ContainsKey(slot))
                representatives[slot] = i;
        }

        var label = RetentionRules.Describe(rule);
        var number = 0;
        foreach (var index in representatives.Values.Reverse())
        {
            if (++number > rule.Keep)
                break;
            reasons[index].Add(new KeepReason(ruleIndex, number, $"{label} #{number}"));
        }
    }
}
