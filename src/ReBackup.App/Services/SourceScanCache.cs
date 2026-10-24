using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Storage;

namespace ReBackup.App.Services;

/// <summary>The answer of <see cref="SourceScanCache.GetAsync"/>: the finished index, or (still running) how far the scan is.</summary>
public sealed record ScanLookup(SourceIndex? Index, bool Running, int Files, int Directories);

/// <summary>
/// Holds the latest completed source scan of up to <see cref="Capacity"/> plans, shared by the Ignore tab and the
/// control service. The Ignore tab hands over the very <see cref="SourceIndex"/> it holds (no copy); a plan that is not
/// cached is scanned on request. An entry nobody has used for <see cref="IdleTimeout"/> is dropped, so that the memory
/// of a large scan comes back. Ignore settings are not part of the key: the patterns are applied to the cached index.
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
    private readonly ITimer _sweeper;
    private long _sequence;

    /// <param name="globalDefaults">The global ignore defaults a scan started by this cache evaluates with.</param>
    public SourceScanCache(IStorageFactory storages, Func<IReadOnlyList<string>> globalDefaults, TimeProvider? time = null)
    {
        _storages = storages;
        _globalDefaults = globalDefaults;
        _time = time ?? TimeProvider.System;
        _sweeper = _time.CreateTimer(_ => DropIdle(), null, SweepInterval, SweepInterval);
    }

    public void Dispose() => _sweeper.Dispose();

    /// <summary>
    /// The scan of the plan's source: the cached one, or a new one started now. Waits up to <paramref name="maxWait"/> for
    /// it; when it is not finished by then, the result is <see cref="ScanLookup.Running"/> with the progress, and the scan
    /// goes on (the next call joins it). A source that is missing or no folder throws a <see cref="StorageException"/>.
    /// </summary>
    public async Task<ScanLookup> GetAsync(BackupPlan plan, TimeSpan maxWait, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            DropIdleLocked();
            if (_entries.TryGetValue(plan.Id, out var existing) && existing.Location is { } location && location != plan.Source)
            {
                Remove(plan.Id, existing);   // the source moved: that scan describes another folder
                existing = null;
            }
            if (existing is null)
            {
                existing = new Entry { Location = plan.Source };
                _entries[plan.Id] = existing;
                var created = existing;
                existing.Starting = Task.Run(() => StartAsync(plan, created));
                EvictOverCapacityLocked(keep: existing);
            }
            existing.Location ??= plan.Source;
            existing.Touch(_time, ++_sequence);
            entry = existing;
        }

        if (entry.Index is { } cached)
            return Finished(cached);

        var work = FinishAsync(entry);
        var finished = await Task.WhenAny(work, Task.Delay(maxWait, _time, ct));
        ct.ThrowIfCancellationRequested();
        if (finished == work)
            return Finished(await work);
        var scan = entry.Scan;
        return new ScanLookup(null, true, scan?.Files ?? 0, scan?.Directories ?? 0);
    }

    /// <summary>Remembers a completed index for the plan (the Ignore tab's own); replaces what was there, even a running scan.</summary>
    /// <param name="source">The source it was scanned from; a later lookup for another source scans again.</param>
    public void Put(string planId, SourceIndex index, StorageLocation? source = null)
    {
        lock (_gate)
        {
            DropIdleLocked();
            if (_entries.TryGetValue(planId, out var old))
                Remove(planId, old);
            var entry = new Entry { Index = index, Location = source };
            entry.Touch(_time, ++_sequence);
            _entries[planId] = entry;
            EvictOverCapacityLocked(keep: entry);
        }
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

    /// <summary>The finished index held for the plan's source, or null (nothing held, still running, or another source). Counts as use.</summary>
    public SourceIndex? TryGetCompleted(string planId, StorageLocation source)
    {
        lock (_gate)
        {
            DropIdleLocked();
            if (!_entries.TryGetValue(planId, out var entry) || entry.Index is not { } index || (entry.Location is { } location && location != source))
                return null;
            entry.Location ??= source;
            entry.Touch(_time, ++_sequence);
            return index;
        }
    }

    /// <summary>Whether a scan (finished or running) of the plan is held.</summary>
    public bool Has(string planId)
    {
        lock (_gate)
        {
            DropIdleLocked();
            return _entries.ContainsKey(planId);
        }
    }

    private static ScanLookup Finished(SourceIndex index) => new(index, false, index.FileCount, index.DirectoryCount);

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
                entry.Index = task.Result;
                entry.Scan = null;
                entry.Touch(_time, ++_sequence);
            }
            else
            {
                Remove(planId, entry);   // failed or canceled: the next lookup tries again
            }
        }
    }

    private static async Task<SourceIndex> FinishAsync(Entry entry)
    {
        var scan = await entry.Starting!.ConfigureAwait(false);
        return await scan.Completion.ConfigureAwait(false);
    }

    private void DropIdle()
    {
        lock (_gate)
            DropIdleLocked();
    }

    private void DropIdleLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var (planId, entry) in _entries.ToList())
        {
            // A running scan is not idle; it counts from its end.
            if (entry.Index is not null && now - entry.LastUsed >= IdleTimeout)
                Remove(planId, entry);
        }
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
