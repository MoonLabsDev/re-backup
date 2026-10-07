using System.Text;

namespace ReBackup.Storage.S3;

/// <summary>Maps storage paths to S3 object keys below the storage's prefix, and back.</summary>
internal static class S3Keys
{
    /// <summary>The longest object key S3 accepts, in UTF-8 bytes.</summary>
    internal const int MaxKeyBytes = 1024;

    /// <summary>Validates the prefix as a storage path; <c>""</c> (the whole bucket) stays <c>""</c>.</summary>
    public static string NormalizePrefix(string prefix) => StoragePath.Validate(prefix);

    /// <summary>The object key of <paramref name="path"/>: prefix + <c>/</c> + path, without leading or double <c>/</c>. Throws <see cref="ArgumentException"/> for an invalid path or a key over 1024 UTF-8 bytes.</summary>
    public static string ToKey(string prefix, string path)
    {
        StoragePath.Validate(path);
        var key = StoragePath.Combine(prefix, path);
        if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
            throw new ArgumentException($"The object key for '{path}' is longer than {MaxKeyBytes} UTF-8 bytes.", nameof(path));
        return key;
    }

    /// <summary>The storage path of an object key below <paramref name="prefix"/>; throws <see cref="ArgumentException"/> for a key outside it.</summary>
    public static string ToPath(string prefix, string key)
    {
        if (prefix.Length == 0) return key;
        if (key.Length <= prefix.Length + 1 || key[prefix.Length] != '/' || !key.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"The key '{key}' is not below the prefix '{prefix}'.", nameof(key));
        return key[(prefix.Length + 1)..];
    }

    /// <summary>The key prefix that all objects below the directory <paramref name="path"/> share: the key + <c>/</c>; <c>""</c> for the root of a whole-bucket storage.</summary>
    public static string DirectoryPrefix(string prefix, string path)
    {
        var key = ToKey(prefix, path);
        return key.Length == 0 ? "" : key + "/";
    }
}
