namespace ReBackup.App.ViewModels;

/// <summary>One row of the retention timeline: the versions one rule keeps.</summary>
public sealed record TimelineLane(string Label, IReadOnlyList<DateTime> Times);
