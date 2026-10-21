using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>A version in the Ignore tab's "Compare with" box; <paramref name="Name"/> null for no comparison.</summary>
public sealed record VersionChoice(string Label, string? Name);

/// <summary>
/// The Ignore tab compared with a version of the plan: what the next backup would hold against what that version holds,
/// to find what grew. The versions are those of the plan's index, which the Versions tab keeps in sync.
/// </summary>
public sealed partial class IgnorePreviewViewModel
{
    private static readonly VersionChoice NoCompare = new("—", null);
    private readonly Func<Func<VersionIndex>?> _versionIndex;
    private readonly Action _requestIndexSync;
    private IReadOnlyList<IndexedVersion> _indexedVersions = [];
    private (string Name, long Id)? _compared;
    private int _choicesGeneration;
    private bool _syncRequested;
    private bool _refreshingChoices;

    [ObservableProperty] private IReadOnlyList<VersionChoice> _compareChoices = [NoCompare];

    /// <summary>The version compared with; <see cref="NoCompare"/> (or null) while nothing is compared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    private VersionChoice? _selectedCompare = NoCompare;

    public bool IsComparing => SelectedCompare?.Name is not null;

    /// <summary>The tab is shown: reads the versions for the box, and asks once for a sync so that the newest are in.</summary>
    public void EnsureCompareChoices()
    {
        if (!_syncRequested && _versionIndex() is not null)
        {
            _syncRequested = true;
            _requestIndexSync();
        }
        RefreshCompareChoices();
    }

    /// <summary>A sync of the index ended: new versions join the box, and new ids replace the compared one's.</summary>
    public void OnIndexSyncEnded() => RefreshCompareChoices();

    partial void OnSelectedCompareChanged(VersionChoice? value)
    {
        if (!_refreshingChoices)
            ApplyComparison();
    }

    private async void RefreshCompareChoices()
    {
        var generation = ++_choicesGeneration;
        IReadOnlyList<IndexedVersion> versions = [];
        if (_versionIndex() is { } open)
        {
            try
            {
                versions = await Task.Run(() => open().Versions()
                    .Where(v => v.Origin != IndexOrigin.Damaged)
                    .OrderByDescending(v => v.LocalTime)
                    .ToList());
            }
            catch (Exception)
            {
                // The index cannot be read: nothing to compare with.
            }
        }
        if (generation != _choicesGeneration)
            return;

        _indexedVersions = versions;
        var current = SelectedCompare?.Name;
        var choices = new List<VersionChoice> { NoCompare };
        choices.AddRange(versions.Select(v =>
            new VersionChoice($"{Formats.DateAndTime(v.LocalTime)} [{Formats.Bytes(v.TotalBytes)}]", v.Name)));
        _refreshingChoices = true;
        try
        {
            CompareChoices = choices;
            SelectedCompare = choices.FirstOrDefault(c => c.Name is not null &&
                                                          string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase)) ?? NoCompare;
        }
        finally
        {
            _refreshingChoices = false;
        }
        ApplyComparison();
    }

    /// <summary>Compares the tree with the chosen version; nothing changes while it is the same version with the same id.</summary>
    private void ApplyComparison()
    {
        var version = SelectedCompare?.Name is { } name
            ? _indexedVersions.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;
        var compared = version is null ? ((string, long)?)null : (version.Name, version.Id);
        if (compared == _compared)
            return;
        _compared = compared;
        Tree.SetComparison(version is not null && _versionIndex() is { } open ? PreviewComparison.Of(open, version) : null);
    }
}
