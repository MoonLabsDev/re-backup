using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>The Cleanup tab: the scan of the checked versions by the saved rules, and when it goes stale (spec 4).</summary>
public sealed partial class CleanupViewModel
{
    /// <summary>How long one look at the source scan waits before the tab shows its progress and looks again.</summary>
    private static readonly TimeSpan SourceWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The current scan; null before the first one and after a cleanup.</summary>
    private ScanState? _scan;

    /// <summary>A cache of the tab's own, for a context without the app's shared one.</summary>
    private SourceScanCache? _ownScanCache;

    /// <summary>
    /// A scan and what it was made from: the rules' fingerprint and the <c>.backupignore</c> files it used, the matcher
    /// the cleanup applies, whether the plan is incremental, and the names of the checked versions.
    /// </summary>
    private sealed record ScanState(string Fingerprint, IReadOnlyList<NestedIgnoreFile> Nested, IgnoreMatcher Matcher,
        bool Pool, CleanupScan Result, HashSet<string> Names);

    /// <summary>Scan is enabled for a saved plan without unsaved edits, with at least one checked row, while nothing runs.</summary>
    private bool CanScan() =>
        !IsScanning && !IsCleaningUp && !_versions.IsDeleting && _savedPlan() is not null && !_hasUnsavedChanges() &&
        Rows.Any(r => r.IsChecked);

    /// <summary>
    /// Scans the checked versions: the source's <c>.backupignore</c> files come from the shared source scan (started
    /// when none is held, its progress shown meanwhile), the rules are the saved plan's (spec 4.1), and the index is
    /// walked on the index worker. Fills each row's Removes and Frees, the by-rule list and the totals.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!CanScan() || _savedPlan() is not { } plan)
            return;
        var rows = Rows.Where(r => r.IsChecked && r.Version.Indexed is not null).ToList();
        var ids = rows.Select(r => r.Version.Indexed!.Id).ToList();
        var names = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        DropScan();
        foreach (var row in Rows)
            row.SetStatus(null);
        _scanHint = null;
        IsScanning = true;
        RefreshHint();
        try
        {
            if (await SourceIndexAsync(plan) is not { } source)
                return;
            var defaults = _globalDefaults().ToList();
            var nested = source.IgnoreFiles;
            var matcher = IgnoreMatcher.ForPlan(plan.Ignore, defaults, nested);
            var fingerprint = RuleFingerprint(plan, defaults, nested);
            var pool = plan.Incremental;
            var result = await _context.Worker.RunAsync(plan.Id, index => CleanupScanner.Scan(index, ids, matcher, pool));

            _scan = new ScanState(fingerprint, nested, matcher, pool, result, names);
            ShowScan(_scan);
            // The rules or the checked rows may have changed while it ran.
            OnPlanChanged();
            OnSelectionChanged();
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            Trace.TraceWarning($"The cleanup scan of plan {plan.Id} failed: {message}");
            _scanHint = new LocText(() => Loc.F("cleanup.scanFailed", ("error", Loc.Known(message))));
        }
        finally
        {
            IsScanning = false;
            RefreshHint();
            NotifyCommands();
        }
    }

    /// <summary>
    /// The finished scan of the plan's source, waiting as long as it runs ("Scanning source… 1,200 files"); null when the
    /// source cannot be read (the hint says why).
    /// </summary>
    private async Task<SourceIndex?> SourceIndexAsync(BackupPlan plan)
    {
        var cache = _context.ScanCache ?? (_ownScanCache ??= new SourceScanCache(_context.Storages, _globalDefaults));
        while (true)
        {
            ScanLookup lookup;
            try
            {
                lookup = await cache.GetAsync(plan, SourceWait, CancellationToken.None);
            }
            catch (StorageException ex)
            {
                var message = ex.Message;
                _scanHint = new LocText(() => Loc.F("cleanup.sourceError", ("error", Loc.Known(message))));
                return null;
            }
            if (lookup.Index is { } index)
            {
                _scanHint = null;
                RefreshHint();
                return index;
            }
            _scanHint = LocText.Of("cleanup.scanningSource", ("files", Formats.Files(lookup.Files)));
            RefreshHint();
        }
    }

    /// <summary>Shows <paramref name="scan"/> in the rows (by name), the by-rule list and the totals.</summary>
    private void ShowScan(ScanState scan)
    {
        var result = scan.Result;
        var byName = RowsByName();
        foreach (var version in result.Versions)
        {
            if (byName.TryGetValue(version.Name, out var row))
                row.ShowScan(version.Files, version.Bytes, scan.Pool);
        }
        foreach (var hit in result.ByRule)
            ByRule.Add(new CleanupRuleRowViewModel(hit));
        var count = result.Versions.Count;
        var files = result.Files;
        var bytes = scan.Pool ? result.PoolBytesFreed ?? 0 : result.Bytes;
        TotalsText = LocText.Of(scan.Pool ? "cleanup.totals.pool" : "cleanup.totals.full", ("count", count),
            ("files", Formats.Files(files)), ("size", Formats.Bytes(bytes)));
        IsStale = false;
    }

    /// <summary>Forgets the scan: the rows' numbers, the by-rule list and the totals.</summary>
    private void DropScan()
    {
        _scan = null;
        foreach (var row in Rows)
            row.ClearScan();
        ByRule.Clear();
        TotalsText = LocText.Empty;
        IsStale = false;
    }

    /// <summary>
    /// A sync of the index has ended: the rows are built again, and the scan goes stale when the manifest stamp of one
    /// of its versions changed (a retry wrote into it, another computer replaced it) or the version is gone.
    /// </summary>
    private void OnIndexSyncEnded()
    {
        Reload();
        _ = CheckStampsAsync();
    }

    private async Task CheckStampsAsync()
    {
        if (_scan is not { } scan || IsStale || _savedPlan() is not { } plan)
            return;
        var names = scan.Result.Versions.Select(v => v.Name).ToList();
        try
        {
            var stamps = await _context.Worker.RunAsync(plan.Id, index =>
            {
                var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var version in index.Versions())
                    ids[version.Name] = version.Id;
                return names.Select(name => ids.TryGetValue(name, out var id) ? index.StampOf(id) : null).ToList();
            });
            if (!ReferenceEquals(_scan, scan) || IsStale)
                return;
            if (scan.Result.Versions.Where((version, i) => version.Stamp != stamps[i]).Any())
            {
                IsStale = true;
                RefreshHint();
            }
        }
        catch (Exception ex)
        {
            // The cleanup compares the stamps itself: a version that changed is skipped there.
            Trace.TraceWarning($"The stamps of the cleanup scan of plan {plan.Id} could not be read: {ex.Message}");
        }
    }
}
