using System.Globalization;

namespace ReBackup.Core.Pool;

/// <summary>Names and paths of the content-addressed pool an incremental plan keeps inside its target.</summary>
public static class PoolLayout
{
    /// <summary>The pool's own metadata file in the target root.</summary>
    public const string StoreFile = "re-store.json";

    /// <summary>The lock file that guards the pool against concurrent writers and garbage collection.</summary>
    public const string LockFile = "re-lock.json";

    /// <summary>The folder in the target root that holds the blobs.</summary>
    public const string PoolFolder = "re-pool";

    /// <summary>Prefix of a pool hash: the 128-bit xxHash of a file's content.</summary>
    public const string HashPrefix = "xxh128:";

    private const int HexLength = 32;

    /// <summary>
    /// The relative path (forward slashes) of the blob for <paramref name="hash"/>: two folder levels of the first
    /// four hex digits, then the full hex as file name.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hash"/> is not "xxh128:" plus 32 hex digits.</exception>
    public static string BlobPath(string hash)
    {
        if (!TryParseHex(hash, out var hex))
            throw new ArgumentException($"Not a pool hash: '{hash}'.", nameof(hash));
        hex = hex.ToLowerInvariant();
        return $"{PoolFolder}/{hex[..2]}/{hex[2..4]}/{hex}";
    }

    /// <summary>"xxh128:" plus 32 lower-case hex digits.</summary>
    public static string Format(UInt128 hash) =>
        HashPrefix + hash.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>Splits "xxh128:" plus 32 hex digits; <paramref name="hex"/> is the digits as written.</summary>
    public static bool TryParseHex(string? hash, out string hex)
    {
        hex = "";
        if (hash is null || hash.Length != HashPrefix.Length + HexLength ||
            !hash.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var digits = hash.AsSpan(HashPrefix.Length);
        foreach (var c in digits)
            if (!char.IsAsciiHexDigit(c))
                return false;
        hex = digits.ToString();
        return true;
    }
}
