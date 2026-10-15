using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>The content of <see cref="PoolLayout.LockFile"/>: who holds a target, since when and for what.</summary>
/// <param name="Host">The computer that holds the lock.</param>
/// <param name="Pid">The process id on that computer.</param>
/// <param name="Operation">One of <c>"backup"</c>, <c>"delete"</c>, <c>"verify"</c>, <c>"copy"</c>.</param>
/// <param name="StartedUtc">When the lock was taken.</param>
/// <param name="Token">A fresh GUID that identifies this holder; only the holder of the token releases the lock.</param>
public sealed record LockInfo(string Host, int Pid, string Operation, DateTime StartedUtc, string Token);

/// <summary>
/// The exclusive lock on a target (<see cref="PoolLayout.LockFile"/>) that serialises backup runs, deletes, garbage
/// collection, verify and plan copy, also across computers. Disposing releases it, but only while the file still holds
/// this lock's own token.
/// </summary>
public sealed class TargetLock : IAsyncDisposable
{
    private readonly IStorage _target;
    private int _released;

    private TargetLock(IStorage target, LockInfo info)
    {
        _target = target;
        Info = info;
    }

    /// <summary>What this lock wrote into the lock file.</summary>
    public LockInfo Info { get; }

    /// <summary>
    /// Takes the lock with an exclusive, durable create. Returns null when another holder has it: the create conflicts,
    /// or the file read back after the commit carries another token (a provider whose exclusive create is check-then-create
    /// can let two writers pass).
    /// </summary>
    /// <exception cref="StorageException">The target cannot be reached or written.</exception>
    public static async Task<TargetLock?> TryAcquireAsync(IStorage target, string host, string operation,
        TimeProvider time, CancellationToken ct)
    {
        var info = new LockInfo(host, Environment.ProcessId, operation, time.GetUtcNow().UtcDateTime,
            Guid.NewGuid().ToString("D"));
        try
        {
            var writer = await target.CreateAsync(PoolLayout.LockFile, new CreateOptions(Overwrite: false, Durable: true), ct)
                .ConfigureAwait(false);
            await using (writer.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(writer, info, JsonDefaults.Options, ct).ConfigureAwait(false);
                await writer.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (StorageConflictException)
        {
            return null;
        }

        var current = await TryReadAsync(target, ct).ConfigureAwait(false);
        return current?.Token == info.Token ? new TargetLock(target, info) : null;
    }

    /// <summary>The current holder; null when the target is not locked.</summary>
    /// <exception cref="JsonException">The lock file is not a lock; <see cref="ForceReleaseAsync"/> removes it.</exception>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    public static async Task<LockInfo?> ReadAsync(IStorage target, CancellationToken ct)
    {
        try
        {
            var stream = await target.OpenReadAsync(PoolLayout.LockFile, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                return await JsonSerializer.DeserializeAsync<LockInfo>(stream, JsonDefaults.Options, ct)
                    .ConfigureAwait(false) ?? throw new JsonException("The lock file is empty.");
        }
        catch (StorageNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Removes the lock whoever holds it, for a lock left behind by a crashed run. No error when there is none.</summary>
    /// <exception cref="StorageException">The target cannot be written.</exception>
    public static Task ForceReleaseAsync(IStorage target, CancellationToken ct) =>
        target.DeleteAsync([PoolLayout.LockFile], ct);

    /// <summary>Releases the lock if the lock file still holds this lock's token; a lock taken over meanwhile is left alone.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        if (await TryReadAsync(_target, CancellationToken.None).ConfigureAwait(false) is { } current && current.Token == Info.Token)
            await _target.DeleteAsync([PoolLayout.LockFile], CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Null when there is no lock file; a lock file that cannot be parsed belongs to nobody we know.</summary>
    private static async Task<LockInfo?> TryReadAsync(IStorage target, CancellationToken ct)
    {
        try
        {
            return await ReadAsync(target, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
