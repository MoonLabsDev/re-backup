using ReBackup.Shared.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>
/// One row of the retention timeline: the versions one rule keeps. <paramref name="Period"/> picks the lane colour
/// (the colour of the rule's badge); null for the lane of versions no rule keeps (the newest one).
/// </summary>
public sealed record TimelineLane(string Label, IReadOnlyList<DateTime> Times, RetentionPeriod? Period = null);
