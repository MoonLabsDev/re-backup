using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Storage;

namespace ReBackup.App.Services;

/// <summary>
/// The answer of <see cref="SourceScanCache.GetAsync"/>: the finished index with the time its scan finished, or (still
/// running) how far the scan is.
/// </summary>
public sealed record ScanLookup(SourceIndex? Index, bool Running, int Files, int Directories, DateTimeOffset? ScannedUtc = null);

/// <summary>A finished scan held by the cache and when it was made.</summary>
public sealed record CachedScan(SourceIndex Index, DateTimeOffset ScannedUtc);

/// <summary>
/// Holds the latest completed source scan of up to <see cref="Capacity"/> plans, shared by the Ignore tab and the
/// control service. The Ignore tab hands over the very <see cref="SourceIndex"/> it holds (no copy); a plan that is not
/// cached is scanned on request. An entry nobody has used for <see cref="IdleTimeout"/> is dropped and the memory is
/// handed back to Windows, so that the memory of a large scan comes back. Ignore settings are not part of the key: the
/// patterns are applied to the cached index. Every finished scan carries the time it was made; a lookup can ask for a
/// new scan (rescan) or for none older than a given time (a newer version exists).
/// </summary>
public sealed class SourceScanCache : IDisposable
{
    public const int Capacity = 2;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly IStorageFactory _storages;
    private readonly Func<IReadOnlyList<string>> _globalDefaults;
    private readonly TimeProvider _time;
    private readonly Action _compactMemory;
    private readonly ITimer _sweeper;
    private long _sequence;

    /// <param name="globalDefaults">The global ignore defaults a scan started by this cache evaluates with.</param>
    /// <param name="compactMemory">
    /// Hands freed memory back after idle scans were dropped; <see cref="MemoryCompaction.Compact"/> by default (tests pass
    /// a counter, so they never force a collection).
    /// </param>
    public SourceScanCache(IStorageFactory storages, Func<IReadOnlyList<string>> globalDefaults, TimeProvider? time = null,
        Action? compactMemory = null)
    {
        _storages = storages;
        _globalDefaults = globalDefaults;
        _time = time ?? TimeProvider.System;
        _compactMemory = compactMemory ?? MemoryCompaction.Compact;
        _sweeper = _time.CreateTimer(_ => DropIdle(), null, SweepInterval, SweepInterval);
    }

    public void Dispose() => _sweeper.Dispose();

    /// <summary>
    /// The scan of the plan's source: the cached one, or a new one started now. Waits up to <paramref name="maxWait"/> for
    /// it; when it is not finished by then, the result is <see cref="ScanLookup.Running"/> with the progress, and the scan
    /// goes on (the next call joins it). A source that is missing or no folder throws a <see cref="StorageException"/>.
    /// </summary>
    /// <param name="rescan">
    /// A finished scan is not good enough: start a new one (a running scan is joined). A scan that a rescan started and
    /// that nobody has received yet (its first lookup answered <see cref="ScanLookup.Running"/>) is taken, not restarted,
    /// so that retrying a rescan comes to an end.
    /// </param>
    /// <param name="staleBefore">A finished scan made before this time is replaced by a new one (a newer version exists).</param>
    public async Task<ScanLookup> GetAsync(BackupPlan plan, TimeSpan maxWait, CancellationToken ct, bool rescan = false,
        DateTimeOffset? staleBefore = null)
    {
        // The scan being waited for can be taken away by someone else (the tab puts its own index, another plan evicts
        // it, Invalidate); that is no cancellation of this caller: look again, the replacement answers (a rescan is not
        // asked for again: the replacement is new).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await GetOnceAsync(plan, maxWait, attempt == 0 && rescan, staleBefore, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
        return new ScanLookup(null, true, 0, 0);
    }

    private async Task<ScanLookup> GetOnceAsync(BackupPlan plan, TimeSpan maxWait, bool rescan, DateTimeOffset? staleBefore,
        CancellationToken ct)
    {
        Entry entry;
        bool dropped;
        lock (_gate)
        {
            dropped = DropIdleLocked();
            if (_entries.TryGetValue(plan.Id, out var existing) && existing.Location is { } location && location != plan.Source)
            {
                Remove(plan.Id, existing);   // the source moved: that scan describes another folder
                existing = null;
            }
            if (existing is { Index: not null, ScannedUtc: { } scannedUtc } finished &&
                (staleBefore is { } limit && scannedUtc < limit || rescan && !(finished.Forced && !finished.Delivered)))
            {
                Remove(plan.Id, finished);   // older than wanted: scan again
                existing = null;
            }
            if (existing is null)
            {
                existing = new Entry { Location = plan.Source, Forced = rescan };
                _entries[plan.Id] = existing;
                var created = existing;
                existing.Starting = Task.Run(() => StartAsync(plan, created));
                EvictOverCapacityLocked(keep: existing);
            }
            existing.Location ??= plan.Source;
            existing.Touch(_time, ++_sequence);
            entry = existing;
        }
        CompactIf(dropped);

        if (entry.Index is not null)
            return Finished(entry);

        var work = FinishAsync(entry);
        var done = await Task.WhenAny(work, Task.Delay(maxWait, _time, ct));
        ct.ThrowIfCancellationRequested();
        if (done == work)
        {
            var index = await work;
            lock (_gate)
                MarkFinished(entry, index);   // the completion callback may not have run yet
            return Finished(entry);
        }
        var scan = entry.Scan;
        return new ScanLookup(null, true, scan?.Files ?? 0, scan?.Directories ?? 0);
    }

    /// <summary>Remembers a completed index for the plan (the Ignore tab's own); replaces what was there, even a running scan.</summary>
    /// <param name="source">The source it was scanned from; a later lookup for another source scans again.</param>
    /// <param name="scannedUtc">When it was scanned; now when left out.</param>
    public void Put(string planId, SourceIndex index, StorageLocation? source = null, DateTimeOffset? scannedUtc = null)
    {
        bool dropped;
        lock (_gate)
        {
            dropped = DropIdleLocked();
            if (_entries.TryGetValue(planId, out var old))
                Remove(planId, old);
            var entry = new Entry { Index = index, Location = source, ScannedUtc = scannedUtc ?? _time.GetUtcNow() };
            entry.Touch(_time, ++_sequence);
            _entries[planId] = entry;
            EvictOverCapacityLocked(keep: entry);
        }
        CompactIf(dropped);
    }

    /// <summary>Forgets the plan's scan (its source changed); a running scan is stopped.</summary>
    public void Invalidate(string planId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(planId, out var entry))
                Remove(planId, entry);
        }
    }

    /// <summary>How many plans are held (for tests: unlike <see cref="Has"/> it does not drop idle entries first).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// The finished scan held for the plan's source and its time, or null (nothing held, still running, or another
    /// source). Counts as use.
    /// </summary>
    public CachedScan? TryGetCompleted(string planId, StorageLocation source)
    {
        CachedScan? found = null;
        bool dropped;
        lock (_gate)
        {
            dropped = DropIdleLocked();
            if (_entries.TryGetValue(planId, out var entry) && entry is { Index: { } index, ScannedUtc: { } scannedUtc } &&
                (entry.Location is not { } location || location == source))
            {
                entry.Location ??= source;
                entry.Touch(_time, ++_sequence);
                found = new CachedScan(index, scannedUtc);
            }
        }
        CompactIf(dropped);
        return found;
    }

    /// <summary>Whether a scan (finished or running) of the plan is held.</summary>
    public bool Has(string planId)
    {
        bool held, dropped;
        lock (_gate)
        {
            dropped = DropIdleLocked();
            held = _entries.ContainsKey(planId);
        }
        CompactIf(dropped);
        return held;
    }

    /// <summary>The answer for a finished entry; it now counts as received (see the rescan of <see cref="GetAsync"/>).</summary>
    private static ScanLookup Finished(Entry entry)
    {
        var index = entry.Index!;
        entry.Delivered = true;
        return new(index, false, index.FileCount, index.DirectoryCount, entry.ScannedUtc);
    }

    private async Task<LiveScan> StartAsync(BackupPlan plan, Entry entry)
    {
        try
        {
            var source = plan.Source;
            if (source.IsFileSystem && string.IsNullOrWhiteSpace(source.Path))
                throw new StorageNotFoundException(source.Path);
            var storage = _storages.Open(source);
            if (await storage.StatAsync("", entry.Cts.Token).ConfigureAwait(false) is not { IsDirectory: true })
                throw new StorageNotFoundException(source.Path);

            var scan = LiveScan.Start(storage, SourceIndexer.DefaultRootName(storage), plan.Ignore,
                _globalDefaults().ToList(), ct: entry.Cts.Token);
            entry.Scan = scan;
            _ = scan.Completion.ContinueWith(task => Completed(plan.Id, entry, task), TaskScheduler.Default);
            return scan;
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(plan.Id, out var current) && ReferenceEquals(current, entry))
                    Remove(plan.Id, entry);
            }
            throw;
        }
    }

    private void Completed(string planId, Entry entry, Task<SourceIndex> task)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(planId, out var current) || !ReferenceEquals(current, entry))
                return;   // replaced or dropped meanwhile
            if (task.IsCompletedSuccessfully)
            {
                MarkFinished(entry, task.Result);
                entry.Touch(_time, ++_sequence);
            }
            else
            {
                Remove(planId, entry);   // failed or canceled: the next lookup tries again
            }
        }
    }

    /// <summary>Records the finished scan on its entry once (under the lock): the index and the time it finished.</summary>
    private void MarkFinished(Entry entry, SourceIndex index)
    {
        if (entry.Index is not null)
            return;
        entry.Index = index;
        entry.ScannedUtc = _time.GetUtcNow();
        entry.Scan = null;
    }

    private static async Task<SourceIndex> FinishAsync(Entry entry)
    {
        var scan = await entry.Starting!.ConfigureAwait(false);
        return await scan.Completion.ConfigureAwait(false);
    }

    private void DropIdle()
    {
        bool dropped;
        lock (_gate)
            dropped = DropIdleLocked();
        CompactIf(dropped);
    }

    /// <summary>Drops the finished scans unused for <see cref="IdleTimeout"/>; true when it dropped any.</summary>
    private bool DropIdleLocked()
    {
        var now = _time.GetUtcNow();
        var dropped = false;
        foreach (var (planId, entry) in _entries.ToList())
        {
            // A running scan is not idle; it counts from its end.
            if (entry.Index is not null && now - entry.LastUsed >= IdleTimeout)
            {
                Remove(planId, entry);
                dropped = true;
            }
        }
        return dropped;
    }

    /// <summary>
    /// After idle scans were dropped (outside the lock): their trees are garbage of the size of a scan; hand that memory
    /// back now rather than whenever a full collection happens to run.
    /// </summary>
    private void CompactIf(bool dropped)
    {
        if (dropped)
            _compactMemory();
    }

    private void EvictOverCapacityLocked(Entry keep)
    {
        while (_entries.Count > Capacity)
        {
            var oldest = _entries.Where(e => !ReferenceEquals(e.Value, keep)).MinBy(e => e.Value.Sequence);
            Remove(oldest.Key, oldest.Value);
        }
    }

    private void Remove(string planId, Entry entry)
    {
        _entries.Remove(planId);
        if (entry.Index is null)
            entry.Cts.Cancel();   // a running scan nobody wants any more
    }

    private sealed class Entry
    {
        public StorageLocation? Location { get; set; }
        public SourceIndex? Index { get; set; }

        /// <summary>When the scan finished (or was put); set together with <see cref="Index"/>.</summary>
        public DateTimeOffset? ScannedUtc { get; set; }

        /// <summary>A rescan started this scan.</summary>
        public bool Forced { get; init; }

        /// <summary>A lookup has received this scan's result.</summary>
        public bool Delivered { get; set; }

        public LiveScan? Scan { get; set; }
        public Task<LiveScan>? Starting { get; set; }
        public CancellationTokenSource Cts { get; } = new();
        public DateTimeOffset LastUsed { get; private set; }
        public long Sequence { get; private set; }

        public void Touch(TimeProvider time, long sequence)
        {
            LastUsed = time.GetUtcNow();
            Sequence = sequence;
        }
    }
}
