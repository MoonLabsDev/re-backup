using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Cleanup;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A line of the Cleanup tab's "By rule" list: a rule, where it comes from, what it removes over the scanned versions and
/// its largest folder or file as an example.
/// </summary>
public sealed class CleanupRuleRowViewModel(CleanupRuleHit hit) : ObservableObject
{
    /// <summary>What the scan found for the rule.</summary>
    public CleanupRuleHit Hit { get; } = hit;

    /// <summary>The rule as written, e.g. <c>target/</c>.</summary>
    public string Rule => Hit.Rule;

    /// <summary>"Global defaults", "Plan", or the path of the <c>.backupignore</c> the rule is in.</summary>
    public string OriginText => PreviewRowViewModel.OriginText(Hit.Origin) ?? Hit.Origin;

    /// <summary>The number of files the rule removes, e.g. "1,204".</summary>
    public string FilesText => Formats.Count(Hit.Files);

    /// <summary>The bytes of those files.</summary>
    public string SizeText => Formats.Bytes(Hit.Bytes);

    /// <summary>The folder or file the rule removes the most bytes in, with its size; empty when there is none.</summary>
    public string LargestText => string.IsNullOrEmpty(Hit.LargestPath)
        ? ""
        : Loc.F("cleanup.byRule.largest", ("path", Hit.LargestPath), ("size", Formats.Bytes(Hit.LargestBytes)));

    /// <summary>The language changed: every text is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
