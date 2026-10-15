using System.Collections.Concurrent;
using System.IO.Hashing;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>What <see cref="PoolWriter.PutAsync"/> did with a file's content.</summary>
public enum PutOutcome
{
    /// <summary>The blob was written and committed.</summary>
    Written,

    /// <summary>The pool already holds the blob; nothing was committed.</summary>
    AlreadyPresent,

    /// <summary>The content read differs from the expected hash (the file changed meanwhile); nothing was committed.</summary>
    ContentChanged,
}

/// <summary>
/// Hashes file content (xxHash128) and writes it as an immutable blob into the pool of a target. Safe to share between
/// parallel workers: the set of known blobs is concurrent, and a blob is only ever created exclusively.
/// </summary>
public sealed class PoolWriter
{
    private readonly IStorage _target;
    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.Ordinal);

    public PoolWriter(IStorage target)
    {
        _target = target;
    }

    /// <summary>
    /// Lists the pool and remembers every blob in it. A target without a pool folder yet is an empty pool; entries
    /// whose file name is not 32 hex digits are ignored.
    /// </summary>
    public async Task LoadKnownAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _target.ListAsync(PoolLayout.PoolFolder, recursive: true, ct).ConfigureAwait(false))
            {
                if (entry.IsDirectory)
                    continue;
                var name = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
                if (PoolLayout.TryParseHex(PoolLayout.HashPrefix + name, out var hex))
                    _known[PoolLayout.HashPrefix + hex.ToLowerInvariant()] = 0;
            }
        }
        catch (StorageNotFoundException)
        {
            // No pool folder yet: nothing is known.
        }
    }

    /// <summary>Whether the pool holds the blob for <paramref name="hash"/> ("xxh128:" plus 32 hex digits, any case).</summary>
    public bool Contains(string hash) => _known.ContainsKey(Normalise(hash));

    /// <summary>Reads <paramref name="input"/> to the end and returns its pool hash and length. <paramref name="onBytes"/> sees every chunk read.</summary>
    public static async Task<(string Hash, long Size)> HashAsync(Stream input, byte[] buffer, Action<int>? onBytes,
        CancellationToken ct)
    {
        var hash = new XxHash128();
        long size = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.Append(buffer.AsSpan(0, read));
            size += read;
            onBytes?.Invoke(read);
        }
        return (PoolLayout.Format(hash.GetCurrentHashAsUInt128()), size);
    }

    /// <summary>
    /// Writes <paramref name="input"/> to the blob named <paramref name="expectedHash"/>, hashing while it writes. Commits only
    /// when the content hashes to the expected value, so a committed blob always matches its name; otherwise the write is
    /// discarded and the result is <see cref="PutOutcome.ContentChanged"/>. A blob that exists already (at create or at
    /// commit) is <see cref="PutOutcome.AlreadyPresent"/>. Blobs carry no modified time: they are shared between versions.
    /// </summary>
    public async Task<PutOutcome> PutAsync(Stream input, string expectedHash, byte[] buffer, Action<int>? onBytes,
        CancellationToken ct)
    {
        var key = Normalise(expectedHash);
        var path = PoolLayout.BlobPath(expectedHash);
        try
        {
            var writer = await _target.CreateAsync(path, new CreateOptions(Overwrite: false), ct).ConfigureAwait(false);
            await using (writer.ConfigureAwait(false))
            {
                var hash = new XxHash128();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    hash.Append(buffer.AsSpan(0, read));
                    await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    onBytes?.Invoke(read);
                }

                if (PoolLayout.Format(hash.GetCurrentHashAsUInt128()) != key)
                    return PutOutcome.ContentChanged;   // disposed uncommitted: discarded
                await writer.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (StorageConflictException)
        {
            _known[key] = 0;
            return PutOutcome.AlreadyPresent;
        }

        _known[key] = 0;
        return PutOutcome.Written;
    }

    private static string Normalise(string hash)
    {
        if (!PoolLayout.TryParseHex(hash, out var hex))
            throw new ArgumentException($"Not a pool hash: '{hash}'.", nameof(hash));
        return PoolLayout.HashPrefix + hex.ToLowerInvariant();
    }
}
