using System.Text.Json;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;

namespace ReBackup.Storage.S3.Connections;

/// <summary>
/// The S3 accounts and connections of one app in a JSON file (<c>formatVersion</c> 2); secrets are stored DPAPI-protected and the
/// plain secret is never written. A missing file is empty. A corrupt file or one of a newer format throws <see cref="JsonException"/>
/// from every member and is never overwritten. Corrupt means: invalid JSON, no format version, no account or connection list, a
/// <c>null</c> entry, an account without id, name or access key ID, a connection without id, name, region, bucket or account id, an
/// id or a name used twice within accounts or within connections (case-insensitive), a connection whose account does not exist,
/// or a region that is no AWS region name.
/// <para>
/// A format 1 file (connections with their own access key) is migrated in memory on every load and written as format 2 by the
/// next save: each entry becomes a connection, entries with the same access key ID share one account that takes the first
/// entry's id, name and secret blob. Names that collide case-insensitively get a " (2)", " (3)", … suffix.
/// </para>
/// An account whose secret cannot be decrypted is kept (loaded with <c>Secret = null</c>) so it survives later saves.
/// </summary>
public sealed class S3ConnectionStore
{
    private const int FormatVersion = 2;

    private readonly string _filePath;

    public S3ConnectionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    // ---- accounts ----

    /// <summary>All accounts in file order.</summary>
    /// <exception cref="JsonException">The file exists but is not valid.</exception>
    public IReadOnlyList<S3Account> LoadAccounts() => Read().Accounts.Select(ToAccount).ToList();

    /// <summary>The account with <paramref name="id"/> (case-insensitive), or <c>null</c>.</summary>
    public S3Account? TryGetAccount(string id) => Read().Accounts.Find(a => SameId(a.Id, id)) is { } entry ? ToAccount(entry) : null;

    /// <summary>
    /// Inserts the account or replaces the one with the same id; a <c>null</c> secret keeps the stored one. Throws
    /// <see cref="ArgumentException"/>, before touching the file, for an account that loading would reject (an empty id, name or
    /// access key ID, a name another account has) and for a new account without a secret.
    /// </summary>
    public void SaveAccount(S3Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (IsBlank(account.Id) || IsBlank(account.Name) || IsBlank(account.AccessKeyId))
            throw new ArgumentException("An account needs an id, a name and an access key ID.", nameof(account));

        var document = Read();
        if (document.Accounts.Exists(a => !SameId(a.Id, account.Id) && SameName(a.Name, account.Name)))
            throw new ArgumentException("Another account already has this name.", nameof(account));
        var index = document.Accounts.FindIndex(a => SameId(a.Id, account.Id));
        if (index < 0 && account.Secret is null)
            throw new ArgumentException("A new account needs a secret.", nameof(account));

        var secretProtected = account.Secret is not null ? SecretProtector.Protect(account.Secret) : document.Accounts[index].SecretProtected;
        var entry = new AccountEntry(account.Id, account.Name, account.AccessKeyId, secretProtected);
        if (index >= 0) document.Accounts[index] = entry; else document.Accounts.Add(entry);
        Write(document);
    }

    /// <summary>
    /// Removes the account with <paramref name="id"/> (case-insensitive); nothing happens when it does not exist. Throws
    /// <see cref="InvalidOperationException"/>, naming the connections, while a connection refers to it.
    /// </summary>
    public void DeleteAccount(string id)
    {
        var document = Read();
        var users = document.Connections.Where(c => SameId(c.AccountId, id)).Select(c => c.Name).ToList();
        if (users.Count > 0)
            throw new InvalidOperationException($"The account is used by these connections: {string.Join(", ", users)}.");
        if (document.Accounts.RemoveAll(a => SameId(a.Id, id)) > 0) Write(document);
    }

    // ---- connections ----

    /// <summary>All connections in file order.</summary>
    /// <exception cref="JsonException">The file exists but is not valid.</exception>
    public IReadOnlyList<S3ConnectionInfo> LoadAll() => Read().Connections.Select(ToInfo).ToList();

    /// <summary>The connection with <paramref name="id"/> (case-insensitive), or <c>null</c>.</summary>
    public S3ConnectionInfo? TryGet(string id) => Read().Connections.Find(c => SameId(c.Id, id)) is { } entry ? ToInfo(entry) : null;

    /// <summary>The connections that refer to the account <paramref name="accountId"/> (case-insensitive), in file order.</summary>
    public IReadOnlyList<S3ConnectionInfo> ConnectionsUsing(string accountId) =>
        Read().Connections.Where(c => SameId(c.AccountId, accountId)).Select(ToInfo).ToList();

    /// <summary>
    /// Inserts the connection or replaces the one with the same id. Throws <see cref="ArgumentException"/>, before touching the file,
    /// for a connection that loading would reject: an empty id, name, region, bucket or account id, a region that is no AWS region
    /// name, a name another connection has, or an account that does not exist.
    /// </summary>
    public void Save(S3ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (IsBlank(connection.Id) || IsBlank(connection.Name) || IsBlank(connection.Region) || IsBlank(connection.Bucket) || IsBlank(connection.AccountId))
            throw new ArgumentException("A connection needs an id, a name, a region, a bucket and an account.", nameof(connection));
        if (!S3Regions.IsValid(connection.Region))
            throw new ArgumentException("The region is no AWS region name.", nameof(connection));

        var document = Read();
        if (!document.Accounts.Exists(a => SameId(a.Id, connection.AccountId)))
            throw new ArgumentException("The account of the connection does not exist.", nameof(connection));
        if (document.Connections.Exists(c => !SameId(c.Id, connection.Id) && SameName(c.Name, connection.Name)))
            throw new ArgumentException("Another connection already has this name.", nameof(connection));

        var entry = new ConnectionEntry(connection.Id, connection.Name, connection.Region, connection.Bucket, connection.AccountId);
        var index = document.Connections.FindIndex(c => SameId(c.Id, connection.Id));
        if (index >= 0) document.Connections[index] = entry; else document.Connections.Add(entry);
        Write(document);
    }

    /// <summary>Removes the connection with <paramref name="id"/> (case-insensitive); nothing happens when it does not exist.</summary>
    public void Delete(string id)
    {
        var document = Read();
        if (document.Connections.RemoveAll(c => SameId(c.Id, id)) > 0) Write(document);
    }

    /// <summary>
    /// The connection with <paramref name="connectionId"/> resolved with its account (see <see cref="S3ConnectionResolver"/>), or
    /// <c>null</c> when there is none. The lookup apps pass to <see cref="S3StorageFactory"/>.
    /// </summary>
    public S3Connection? TryResolve(string connectionId)
    {
        var document = Read();
        if (document.Connections.Find(c => SameId(c.Id, connectionId)) is not { } connection) return null;
        var account = document.Accounts.Find(a => SameId(a.Id, connection.AccountId));
        return S3ConnectionResolver.Resolve(ToInfo(connection), account is null ? null : ToAccount(account));
    }

    // ---- file ----

    private static S3Account ToAccount(AccountEntry e) => new(e.Id, e.Name, e.AccessKeyId, SecretProtector.TryUnprotect(e.SecretProtected));

    private static S3ConnectionInfo ToInfo(ConnectionEntry e) => new(e.Id, e.Name, e.Region, e.Bucket, e.AccountId);

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private Document Read()
    {
        if (!File.Exists(_filePath)) return new Document([], []);
        var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(_filePath), JsonDefaults.Options)
                   ?? throw new JsonException("The connections file is empty.");
        if (file.FormatVersion > FormatVersion)
            throw new JsonException($"The connections file was written by a newer version (format {file.FormatVersion}); this version reads format {FormatVersion} and leaves the file unchanged.");
        if (file.FormatVersion < 1) throw new JsonException("The connections file is corrupt: it has no format version.");

        var document = file.FormatVersion == 1 ? Migrate(file) : ReadV2(file);
        Validate(document);
        return document;
    }

    private static Document ReadV2(FileDto file)
    {
        var accountDtos = file.Accounts ?? throw Corrupt("it has no account list");
        var connectionDtos = file.Connections ?? throw Corrupt("it has no connection list");
        var accounts = accountDtos.Select((a, i) =>
            a is null || IsBlank(a.Id) || IsBlank(a.Name) || IsBlank(a.AccessKeyId)
                ? throw Corrupt($"account {i + 1} lacks a required field")
                : new AccountEntry(a.Id!, a.Name!, a.AccessKeyId!, a.SecretProtected)).ToList();
        var connections = connectionDtos.Select((c, i) =>
            c is null || IsBlank(c.Id) || IsBlank(c.Name) || IsBlank(c.Region) || IsBlank(c.Bucket) || IsBlank(c.AccountId)
                ? throw Corrupt($"connection {i + 1} lacks a required field")
                : new ConnectionEntry(c.Id!, c.Name!, c.Region!, c.Bucket!, c.AccountId!)).ToList();
        return new Document(accounts, connections);
    }

    /// <summary>Format 1 → format 2 in memory; see the class remarks. The secret blobs are carried over unchanged, never decrypted.</summary>
    private static Document Migrate(FileDto file)
    {
        var entries = file.Connections ?? throw Corrupt("it has no connection list");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e is null || IsBlank(e.Id) || IsBlank(e.Name) || IsBlank(e.Region) || IsBlank(e.Bucket) || IsBlank(e.AccessKeyId))
                throw Corrupt($"connection {i + 1} lacks a required field");
            if (!ids.Add(e.Id!)) throw Corrupt($"the id of connection {i + 1} is used twice");
        }

        var accounts = new List<AccountEntry>();
        var connections = new List<ConnectionEntry>();
        var accountByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var accountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!accountByKey.TryGetValue(e!.AccessKeyId!, out var accountId))
            {
                accountId = e.Id!;
                accountByKey.Add(e.AccessKeyId!, accountId);
                accounts.Add(new AccountEntry(accountId, UniqueName(e.Name!, accountNames), e.AccessKeyId!, e.SecretProtected));
            }
            connections.Add(new ConnectionEntry(e.Id!, UniqueName(e.Name!, connectionNames), e.Region!, e.Bucket!, accountId));
        }
        return new Document(accounts, connections);
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }

    private static void Validate(Document document)
    {
        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < document.Accounts.Count; i++)
        {
            if (!accountIds.Add(document.Accounts[i].Id)) throw Corrupt($"the id of account {i + 1} is used twice");
            if (!accountNames.Add(document.Accounts[i].Name)) throw Corrupt($"the name of account {i + 1} is used twice");
        }

        var connectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < document.Connections.Count; i++)
        {
            var c = document.Connections[i];
            if (!connectionIds.Add(c.Id)) throw Corrupt($"the id of connection {i + 1} is used twice");
            if (!connectionNames.Add(c.Name)) throw Corrupt($"the name of connection {i + 1} is used twice");
            if (!accountIds.Contains(c.AccountId)) throw Corrupt($"the account of connection {i + 1} does not exist");
            if (!S3Regions.IsValid(c.Region)) throw Corrupt($"the region of connection {i + 1} is no AWS region name");
        }
    }

    private static JsonException Corrupt(string reason) => new($"The connections file is corrupt: {reason}.");

    private void Write(Document document) =>
        AtomicFile.WriteAllText(_filePath,
            JsonSerializer.Serialize(new FileV2(FormatVersion, document.Accounts, document.Connections), JsonDefaults.Options));

    private sealed record Document(List<AccountEntry> Accounts, List<ConnectionEntry> Connections);

    /// <summary>Read side of both formats: format 1 connections carry their own key, format 2 ones an account id.</summary>
    private sealed record FileDto(int FormatVersion, List<AccountDto?>? Accounts, List<ConnectionDto?>? Connections);

    private sealed record AccountDto(string? Id, string? Name, string? AccessKeyId, string? SecretProtected);

    private sealed record ConnectionDto(string? Id, string? Name, string? Region, string? Bucket, string? AccountId, string? AccessKeyId, string? SecretProtected);

    /// <summary>Write side: always format 2.</summary>
    private sealed record FileV2(int FormatVersion, List<AccountEntry> Accounts, List<ConnectionEntry> Connections);

    private sealed record AccountEntry(string Id, string Name, string AccessKeyId, string? SecretProtected);

    private sealed record ConnectionEntry(string Id, string Name, string Region, string Bucket, string AccountId);
}
