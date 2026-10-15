using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>The content of <see cref="PoolLayout.StoreFile"/>: which storage format a target holds.</summary>
public sealed record StoreDescriptor(string Format, string Hash, int Version, string PlanId);

/// <summary>What <see cref="StoreGuard.CheckAsync"/> found.</summary>
public enum StoreGuardOutcome
{
    /// <summary>The target holds this plan's pool.</summary>
    Ok,

    /// <summary>The target held nothing of the plan; the pool's store file was written (or would be).</summary>
    Created,

    /// <summary>A full plan points at a target that holds a pool.</summary>
    FullPlanOnPoolTarget,

    /// <summary>An incremental plan points at a target that already holds full versions of it.</summary>
    PoolPlanOnFullTarget,

    /// <summary>The target holds the pool of another plan.</summary>
    OtherPlan,

    /// <summary>The store file or the target's layout is one this ReBackup does not know.</summary>
    UnsupportedFormat,

    /// <summary>
    /// An incremental plan points at a target without a store file that holds backups of another plan (a folder named
    /// like a version, also one being written or deleted, or a folder with a manifest).
    /// </summary>
    TargetNotEmpty,
}

/// <summary>Makes sure a target holds the storage format a plan expects, before a run, a copy or a view touches it.</summary>
public static class StoreGuard
{
    private const string PoolFormat = "pool";
    private const string PoolHash = "xxh128";
    private const int PoolVersion = 1;

    /// <summary>
    /// Compares the target with the plan. For an incremental plan on an empty target (no store file, no pool folder and
    /// nothing <see cref="IsEmptyAsync"/> counts as a backup) the store file is written exclusively and durably when <paramref name="createIfMissing"/>
    /// is set; otherwise <see cref="StoreGuardOutcome.Created"/> is returned without writing anything. Nothing is
    /// written for any other outcome.
    /// </summary>
    /// <exception cref="StorageUnavailableException">The target cannot be reached.</exception>
    /// <exception cref="StorageException">The target cannot be read or written.</exception>
    public static async Task<StoreGuardOutcome> CheckAsync(IStorage target, BackupPlan plan, bool createIfMissing,
        CancellationToken ct = default)
    {
        var existing = await TryReadAsync(target, ct).ConfigureAwait(false);
        if (existing is not null)
            return Evaluate(existing, plan);
        if (!plan.Incremental)
            return StoreGuardOutcome.Ok;

        if ((await VersionCatalog.ListAsync(target, plan.Id, plan.Name, ct).ConfigureAwait(false)).Count > 0)
            return StoreGuardOutcome.PoolPlanOnFullTarget;
        if (await target.StatAsync(PoolLayout.PoolFolder, ct).ConfigureAwait(false) is not null)
            return StoreGuardOutcome.UnsupportedFormat;   // a pool folder without its descriptor: not ours to adopt
        // Another plan's versions: a store file here would stop every run of that (full) plan.
        if (!await IsEmptyAsync(target, ct).ConfigureAwait(false))
            return StoreGuardOutcome.TargetNotEmpty;
        if (!createIfMissing)
            return StoreGuardOutcome.Created;

        var descriptor = new StoreDescriptor(PoolFormat, PoolHash, PoolVersion, plan.Id);
        try
        {
            var writer = await target.CreateAsync(PoolLayout.StoreFile, new CreateOptions(Durable: true), ct)
                .ConfigureAwait(false);
            await using (writer.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(writer, descriptor, JsonDefaults.Options, ct).ConfigureAwait(false);
                await writer.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (StorageConflictException)
        {
            // Another computer created the store meanwhile: judge what it wrote.
            return await TryReadAsync(target, ct).ConfigureAwait(false) is { } raced
                ? Evaluate(raced, plan)
                : StoreGuardOutcome.UnsupportedFormat;
        }
        return StoreGuardOutcome.Created;
    }

    /// <summary>The target's store descriptor; null when there is none.</summary>
    /// <exception cref="JsonException">The store file is not a descriptor.</exception>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    public static async Task<StoreDescriptor?> ReadAsync(IStorage target, CancellationToken ct = default)
    {
        try
        {
            var stream = await target.OpenReadAsync(PoolLayout.StoreFile, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                return await JsonSerializer.DeserializeAsync<StoreDescriptor>(stream, JsonDefaults.Options, ct)
                    .ConfigureAwait(false) ?? throw new JsonException("The store file is empty.");
        }
        catch (StorageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The <c>core.run.*</c> key of the message that explains an outcome that aborts a run.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is <see cref="StoreGuardOutcome.Ok"/> or <see cref="StoreGuardOutcome.Created"/>.</exception>
    public static string MessageKey(StoreGuardOutcome outcome) => outcome switch
    {
        StoreGuardOutcome.FullPlanOnPoolTarget => "core.run.storeFullOnPool",
        StoreGuardOutcome.PoolPlanOnFullTarget => "core.run.storePoolOnFull",
        StoreGuardOutcome.OtherPlan => "core.run.storeOtherPlan",
        StoreGuardOutcome.UnsupportedFormat => "core.run.storeUnsupported",
        StoreGuardOutcome.TargetNotEmpty => "core.run.storeNotEmpty",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "This outcome has no message."),
    };

    /// <summary>Null when there is no store file; a store file that is not a descriptor reads as one of no known format.</summary>
    private static async Task<StoreDescriptor?> TryReadAsync(IStorage target, CancellationToken ct)
    {
        try
        {
            return await ReadAsync(target, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new StoreDescriptor("", "", 0, "");
        }
    }

    /// <summary>
    /// Whether a target holds nothing of any plan: no store file, no pool folder, no folder named like a version (also one
    /// being written or deleted) and no folder with a manifest (<see cref="VersionMarkerNames.Manifest"/>, a version
    /// renamed by hand). Other files and folders do not count. A target that does not exist yet is empty.
    /// </summary>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    internal static async Task<bool> IsEmptyAsync(IStorage target, CancellationToken ct)
    {
        if (await target.StatAsync(PoolLayout.StoreFile, ct).ConfigureAwait(false) is not null)
            return false;
        var folders = new List<string>();
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                    continue;
                var name = StoragePath.Name(entry.Path);
                foreach (var suffix in (ReadOnlySpan<string>)[VersionName.PartialSuffix, VersionName.DeletingSuffix])
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        name = name[..^suffix.Length];
                if (name.Equals(PoolLayout.PoolFolder, StringComparison.OrdinalIgnoreCase) ||
                    VersionName.TryParseAny(name, out _, out _))
                    return false;
                folders.Add(entry.Path);
            }
        }
        catch (StorageNotFoundException)
        {
            return true;   // not created yet
        }
        foreach (var folder in folders)
            if (await target.StatAsync(StoragePath.Combine(folder, VersionMarkerNames.Manifest), ct).ConfigureAwait(false) is not null)
                return false;
        return true;
    }

    private static StoreGuardOutcome Evaluate(StoreDescriptor store, BackupPlan plan)
    {
        if (!plan.Incremental)
            return StoreGuardOutcome.FullPlanOnPoolTarget;
        if (store.PlanId.Length > 0 && !string.Equals(store.PlanId, plan.Id, StringComparison.OrdinalIgnoreCase))
            return StoreGuardOutcome.OtherPlan;
        return store.PlanId.Length > 0 && store.Format == PoolFormat && store.Hash == PoolHash && store.Version == PoolVersion
            ? StoreGuardOutcome.Ok
            : StoreGuardOutcome.UnsupportedFormat;
    }
}
