using System.Text.Json;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;

namespace ReBackup.Storage.S3.Connections;

/// <summary>
/// The connections of one app in a JSON file; secrets are stored DPAPI-protected and the plain secret is never written.
/// A missing file is an empty list. A corrupt file (invalid JSON, no format version, an entry without id, name, region, bucket or
/// access key ID, an id used twice) or one of a newer format throws <see cref="JsonException"/> and is never overwritten.
/// An entry whose secret cannot be decrypted is kept (loaded with <c>Secret = null</c>) so it survives later saves.
/// </summary>
public sealed class S3ConnectionStore
{
    private const int FormatVersion = 1;

    private readonly string _filePath;

    public S3ConnectionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>All connections in file order.</summary>
    /// <exception cref="JsonException">The file exists but is not valid.</exception>
    public IReadOnlyList<S3Connection> LoadAll() =>
        ReadEntries().Select(e => new S3Connection(e.Id, e.Name, e.Region, e.Bucket, e.AccessKeyId,
            SecretProtector.TryUnprotect(e.SecretProtected))).ToList();

    /// <summary>The connection with <paramref name="id"/> (case-insensitive), or <c>null</c>.</summary>
    public S3Connection? TryGet(string id) =>
        LoadAll().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Inserts the connection or replaces the one with the same id; a <c>null</c> secret keeps the stored one.</summary>
    public void Save(S3Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var entries = ReadEntries();
        var index = entries.FindIndex(e => SameId(e.Id, connection.Id));
        var secretProtected = connection.Secret is not null
            ? SecretProtector.Protect(connection.Secret)
            : index >= 0 ? entries[index].SecretProtected : null;
        var entry = new Entry(connection.Id, connection.Name, connection.Region, connection.Bucket,
            connection.AccessKeyId, secretProtected);
        if (index >= 0) entries[index] = entry; else entries.Add(entry);
        Write(entries);
    }

    /// <summary>Removes the connection with <paramref name="id"/> (case-insensitive); nothing happens when it does not exist.</summary>
    public void Delete(string id)
    {
        var entries = ReadEntries();
        if (entries.RemoveAll(e => SameId(e.Id, id)) > 0) Write(entries);
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private List<Entry> ReadEntries()
    {
        if (!File.Exists(_filePath)) return [];
        var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(_filePath), JsonDefaults.Options)
                   ?? throw new JsonException("The connections file is empty.");
        if (file.FormatVersion > FormatVersion)
            throw new JsonException($"The connections file was written by a newer version (format {file.FormatVersion}); this version reads format {FormatVersion} and leaves the file unchanged.");
        if (file.FormatVersion < 1) throw new JsonException("The connections file is corrupt: it has no format version.");

        var entries = file.Connections?.ToList() ?? [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e is null || IsBlank(e.Id) || IsBlank(e.Name) || IsBlank(e.Region) || IsBlank(e.Bucket) || IsBlank(e.AccessKeyId))
                throw new JsonException($"The connections file is corrupt: connection {i + 1} lacks a required field.");
            if (!ids.Add(e.Id)) throw new JsonException($"The connections file is corrupt: the id of connection {i + 1} is used twice.");
        }
        return entries;

        static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
    }

    private void Write(List<Entry> entries) =>
        AtomicFile.WriteAllText(_filePath,
            JsonSerializer.Serialize(new FileDto(FormatVersion, entries), JsonDefaults.Options));

    private sealed record FileDto(int FormatVersion, List<Entry>? Connections);

    private sealed record Entry(string Id, string Name, string Region, string Bucket, string AccessKeyId, string? SecretProtected);
}
