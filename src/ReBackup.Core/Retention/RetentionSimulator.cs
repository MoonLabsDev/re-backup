using System.Globalization;

namespace ReBackup.Core.Retention;

/// <summary>A version that is still there at the end of a simulation. No reasons: the next run would delete it.</summary>
public sealed record SimulatedVersion(DateTime LocalTime, bool Existing, IReadOnlyList<KeepReason> Reasons);

/// <summary>
/// <paramref name="SteadyStateCount"/> is the largest number of versions after a run in the last simulated year.
/// <paramref name="Survivors"/> are the versions at the horizon, oldest first.
/// </summary>
public sealed record SimulationResult(int SteadyStateCount, long? EstimatedBytes,
    IReadOnlyList<SimulatedVersion> Survivors, int RunsSimulated, bool Truncated, DateTime Horizon);

/// <summary>Plays future runs against the retention rules to show what a plan needs at full extension.</summary>
public static class RetentionSimulator
{
    public const int MaxRuns = 20_000;
    public const int HorizonYears = 2;

    private const string SimulatedPrefix = "simulated ";

    /// <summary>An endless series of run times.</summary>
    public static IEnumerable<DateTime> Every(DateTime first, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
        return Series(first, interval);
    }

    private static IEnumerable<DateTime> Series(DateTime first, TimeSpan interval)
    {
        for (var time = first; ; time += interval)
            yield return time;
    }

    /// <param name="futureRuns">Run times in ascending order; times up to <paramref name="now"/> are ignored.</param>
    /// <param name="averageVersionBytes">Size of one version for the estimate; null when unknown.</param>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static SimulationResult Simulate(IReadOnlyList<RetentionVersion> existing, IReadOnlyList<RetentionRule> rules,
        IEnumerable<DateTime> futureRuns, DateTime now, long? averageVersionBytes,
        CancellationToken cancellationToken = default)
    {
        var horizon = now.AddYears(HorizonYears);
        var lastYear = horizon.AddYears(-1);
        var current = existing.ToList();
        var steady = 0;
        var sawLastYear = false;
        var runs = 0;
        var truncated = false;

        foreach (var run in futureRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run <= now)
                continue;
            if (run > horizon)
                break;
            if (runs == MaxRuns)
            {
                truncated = true;
                break;
            }

            runs++;
            current.Add(new RetentionVersion(SimulatedPrefix + run.Ticks.ToString(CultureInfo.InvariantCulture), run));
            if (rules.Count > 0)
            {
                // Without rules nothing is ever deleted, so there is nothing to evaluate per run.
                current = RetentionEngine.Evaluate(current, rules).Where(d => d.Keep).Select(d => d.Version).ToList();
            }

            if (run > lastYear)
            {
                steady = Math.Max(steady, current.Count);
                sawLastYear = true;
            }
        }

        var final = RetentionEngine.Evaluate(current, rules);
        if (!sawLastYear)
            steady = final.Count;

        var survivors = final
            .Select(d => new SimulatedVersion(d.Version.LocalTime,
                !d.Version.Name.StartsWith(SimulatedPrefix, StringComparison.Ordinal), d.Reasons))
            .ToArray();
        long? estimated = averageVersionBytes is { } average ? average * steady : null;
        return new SimulationResult(steady, estimated, survivors, runs, truncated, horizon);
    }
}
