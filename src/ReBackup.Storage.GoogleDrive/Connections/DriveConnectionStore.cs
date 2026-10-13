using System.Text.Json;
using System.Text.Json.Nodes;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;

namespace ReBackup.Storage.GoogleDrive.Connections;

/// <summary>
/// The Drive accounts and connections of one app in a JSON file (<c>formatVersion</c> 1); the service-account key is stored
/// DPAPI-protected and the plain key is never written. A missing file is empty. A corrupt file or one of a newer format throws
/// <see cref="JsonException"/> from every member except <see cref="TryResolve"/> (which throws <see cref="StorageIOException"/> with the
/// same reason) and is never overwritten. Corrupt means: invalid JSON, no format version, no account or connection list, a
/// <c>null</c> entry, an account without id, name or client e-mail, a connection without id, name, account id, root id or root name,
/// an id or a name used twice within accounts or within connections (case-insensitive), or a connection whose account does not exist.
/// An account whose key cannot be decrypted (another Windows account) is kept, so it survives later saves; see <see cref="DriveAccount.NeedsKey"/>.
/// </summary>
public sealed class DriveConnectionStore
{
    private const int FormatVersion = 1;

    private readonly string _filePath;

    public DriveConnectionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    // ---- accounts ----

    /// <summary>All accounts in file order.</summary>
    /// <exception cref="JsonException">The file exists but is not valid.</exception>
    public IReadOnlyList<DriveAccount> LoadAccounts() => Read().Accounts.Select(ToAccount).ToList();

    /// <summary>The account with <paramref name="id"/> (case-insensitive), or <c>null</c>.</summary>
    public DriveAccount? TryGetAccount(string id) => Read().Accounts.Find(a => SameId(a.Id, id)) is { } entry ? ToAccount(entry) : null;

    /// <summary>
    /// Inserts the account or replaces the one with the same id. <paramref name="keyJson"/> is the service-account key file; its
    /// <c>client_email</c> becomes the account's e-mail. A <c>null</c> key keeps the stored one (rename). Throws
    /// <see cref="ArgumentException"/>, before touching the file, for a key that is not a service-account key (never quoting it), an
    /// empty id or name, a name another account has, and a new account without a key.
    /// </summary>
    public void SaveAccount(string id, string name, string? keyJson)
    {
        if (IsBlank(id) || IsBlank(name)) throw new ArgumentException("An account needs an id and a name.");
        var clientEmail = keyJson is null ? null : ReadClientEmail(keyJson);

        var document = Read();
        if (document.Accounts.Exists(a => !SameId(a.Id, id) && SameName(a.Name, name)))
            throw new ArgumentException("Another account already has this name.", nameof(name));
        var index = document.Accounts.FindIndex(a => SameId(a.Id, id));
        if (index < 0 && keyJson is null)
            throw new ArgumentException("A new account needs a service account key.", nameof(keyJson));

        var entry = keyJson is not null
            ? new AccountEntry(id, name, clientEmail!, SecretProtector.Protect(keyJson))
            : document.Accounts[index] with { Id = id, Name = name };
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
    public IReadOnlyList<DriveConnectionInfo> LoadAll() => Read().Connections.Select(ToInfo).ToList();

    /// <summary>The connection with <paramref name="id"/> (case-insensitive), or <c>null</c>.</summary>
    public DriveConnectionInfo? TryGet(string id) => Read().Connections.Find(c => SameId(c.Id, id)) is { } entry ? ToInfo(entry) : null;

    /// <summary>The connections that refer to the account <paramref name="accountId"/> (case-insensitive), in file order.</summary>
    public IReadOnlyList<DriveConnectionInfo> ConnectionsUsing(string accountId) =>
        Read().Connections.Where(c => SameId(c.AccountId, accountId)).Select(ToInfo).ToList();

    /// <summary>
    /// Inserts the connection or replaces the one with the same id. Throws <see cref="ArgumentException"/>, before touching the file,
    /// for a connection that loading would reject: an empty id, name, account id, root id or root name, a name another connection
    /// has, or an account that does not exist. A blank <see cref="DriveConnectionInfo.SharedDriveId"/> is stored as <c>null</c>.
    /// </summary>
    public void Save(DriveConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (IsBlank(connection.Id) || IsBlank(connection.Name) || IsBlank(connection.AccountId) || IsBlank(connection.RootId) || IsBlank(connection.RootName))
            throw new ArgumentException("A connection needs an id, a name, an account and a root.", nameof(connection));

        var document = Read();
        if (!document.Accounts.Exists(a => SameId(a.Id, connection.AccountId)))
            throw new ArgumentException("The account of the connection does not exist.", nameof(connection));
        if (document.Connections.Exists(c => !SameId(c.Id, connection.Id) && SameName(c.Name, connection.Name)))
            throw new ArgumentException("Another connection already has this name.", nameof(connection));

        var entry = new ConnectionEntry(connection.Id, connection.Name, connection.AccountId, connection.RootId, connection.RootName,
            IsBlank(connection.SharedDriveId) ? null : connection.SharedDriveId);
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
    /// The connection with <paramref name="connectionId"/> with its account's key, or <c>null</c> when there is none. The lookup apps
    /// pass to the Drive storage factory. Throws <see cref="StorageIOException"/> (path = the connection id) when the file is corrupt
    /// or of a newer format, and when the account's key cannot be decrypted on this Windows account (it has to be entered again).
    /// </summary>
    public DriveConnection? TryResolve(string connectionId)
    {
        Document document;
        try
        {
            document = Read();
        }
        catch (JsonException ex)
        {
            throw new StorageIOException(connectionId, ex.Message, ex);
        }

        if (document.Connections.Find(c => SameId(c.Id, connectionId)) is not { } connection) return null;
        var account = document.Accounts.Find(a => SameId(a.Id, connection.AccountId))!; // Validate guarantees it exists.
        var key = SecretProtector.TryUnprotect(account.ProtectedKey)
                  ?? throw new StorageIOException(connectionId,
                      $"The key of the Google account '{account.Name}' cannot be read on this Windows account; enter the key again in the account settings.");
        return new DriveConnection(ToInfo(connection), account.ClientEmail, key);
    }

    // ---- key ----

    /// <summary>The <c>client_email</c> of a service-account key; never quotes the key in the exception.</summary>
    private static string ReadClientEmail(string keyJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(keyJson);
        }
        catch (JsonException)
        {
            throw NotAServiceAccountKey();
        }

        if (node is not JsonObject obj) throw NotAServiceAccountKey();
        if (!(obj["type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "service_account")) throw NotAServiceAccountKey();
        if (!(obj["private_key"] is JsonValue pk && pk.TryGetValue<string>(out var privateKey) && !IsBlank(privateKey))) throw NotAServiceAccountKey();
        if (!(obj["client_email"] is JsonValue ce && ce.TryGetValue<string>(out var email) && !IsBlank(email))) throw NotAServiceAccountKey();
        return email.Trim();
    }

    private static ArgumentException NotAServiceAccountKey() =>
        new("This is not a Google service account key. Download the JSON key of a service account (type \"service_account\") from the Google Cloud console.", "keyJson");

    // ---- file ----

    private static DriveAccount ToAccount(AccountEntry e) => new(e.Id, e.Name, e.ClientEmail, e.ProtectedKey);

    private static DriveConnectionInfo ToInfo(ConnectionEntry e) => new(e.Id, e.Name, e.AccountId, e.RootId, e.RootName, e.SharedDriveId);

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private Document Read()
    {
        if (!File.Exists(_filePath)) return new Document([], []);
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(_filePath), JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"The connections file is corrupt: it is not valid JSON ({ex.Message})", ex);
        }
        if (file is null) throw new JsonException("The connections file is empty.");
        if (file.FormatVersion > FormatVersion)
            throw new JsonException($"The connections file was written by a newer version (format {file.FormatVersion}); this version reads format {FormatVersion} and leaves the file unchanged.");
        if (file.FormatVersion < 1) throw new JsonException("The connections file is corrupt: it has no format version.");

        var accountDtos = file.Accounts ?? throw Corrupt("it has no account list");
        var connectionDtos = file.Connections ?? throw Corrupt("it has no connection list");
        var accounts = accountDtos.Select((a, i) =>
            a is null || IsBlank(a.Id) || IsBlank(a.Name) || IsBlank(a.ClientEmail)
                ? throw Corrupt($"account {i + 1} lacks a required field")
                : new AccountEntry(a.Id!, a.Name!, a.ClientEmail!, a.ProtectedKey)).ToList();
        var connections = connectionDtos.Select((c, i) =>
            c is null || IsBlank(c.Id) || IsBlank(c.Name) || IsBlank(c.AccountId) || IsBlank(c.RootId) || IsBlank(c.RootName)
                ? throw Corrupt($"connection {i + 1} lacks a required field")
                : new ConnectionEntry(c.Id!, c.Name!, c.AccountId!, c.RootId!, c.RootName!, IsBlank(c.SharedDriveId) ? null : c.SharedDriveId)).ToList();
        var document = new Document(accounts, connections);
        Validate(document);
        return document;
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
        }
    }

    private static JsonException Corrupt(string reason) => new($"The connections file is corrupt: {reason}.");

    private void Write(Document document) =>
        AtomicFile.WriteAllText(_filePath,
            JsonSerializer.Serialize(new FileOut(FormatVersion, document.Accounts.Select(a => new AccountDto(a.Id, a.Name, a.ClientEmail, a.ProtectedKey)).ToList(),
                document.Connections.Select(c => new ConnectionDto(c.Id, c.Name, c.AccountId, c.RootId, c.RootName, c.SharedDriveId)).ToList()),
                JsonDefaults.Options));

    private sealed record Document(List<AccountEntry> Accounts, List<ConnectionEntry> Connections);

    private sealed record FileDto(int FormatVersion, List<AccountDto?>? Accounts, List<ConnectionDto?>? Connections);

    private sealed record FileOut(int FormatVersion, List<AccountDto> Accounts, List<ConnectionDto> Connections);

    private sealed record AccountDto(string? Id, string? Name, string? ClientEmail, string? ProtectedKey);

    private sealed record ConnectionDto(string? Id, string? Name, string? AccountId, string? RootId, string? RootName, string? SharedDriveId);

    private sealed record AccountEntry(string Id, string Name, string ClientEmail, string? ProtectedKey);

    private sealed record ConnectionEntry(string Id, string Name, string AccountId, string RootId, string RootName, string? SharedDriveId);
}
