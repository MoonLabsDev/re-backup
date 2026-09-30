using ReBackup.Core.Backup;

namespace ReBackup.Core.Retention;

/// <summary>A version folder and what retention decides for it. No decision: retention does not manage the folder.</summary>
public sealed record VersionDecision(VersionInfo Version, RetentionDecision? Decision)
{
    /// <summary>True when retention deletes this version.</summary>
    public bool Delete => Decision is { Keep: false };
}

/// <summary>Applies retention rules to the version folders of a target.</summary>
public static class RetentionPlanner
{
    private const string UpcomingName = "\0upcoming";

    /// <summary>
    /// Decides for every owned version; the result has the order of <paramref name="versions"/>. With
    /// <paramref name="upcomingRun"/> the decision is made as if a version of that time already existed.
    /// </summary>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static IReadOnlyList<VersionDecision> Decide(IReadOnlyList<VersionInfo> versions,
        IReadOnlyList<RetentionRule> rules, DateTime? upcomingRun = null)
    {
        var input = versions.Where(v => v.IsOwned).Select(v => new RetentionVersion(v.Name, v.LocalTime)).ToList();
        if (upcomingRun is { } time)
            input.Add(new RetentionVersion(UpcomingName, time));

        var byName = new Dictionary<string, RetentionDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in RetentionEngine.Evaluate(input, rules))
            byName[decision.Version.Name] = decision;

        return versions.Select(v => new VersionDecision(v, v.IsOwned ? byName[v.Name] : null)).ToArray();
    }
}
