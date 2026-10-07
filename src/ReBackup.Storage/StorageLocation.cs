using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Storage;

/// <summary>
/// Where a backup source or target lives: the <paramref name="Kind"/> of storage, a <paramref name="Path"/> that is
/// meaningful for that kind (a local folder for <c>"fs"</c>), and optionally which saved connection to use.
/// </summary>
[JsonConverter(typeof(StorageLocationJsonConverter))]
public sealed record StorageLocation(string Kind, string Path, string? ConnectionId = null)
{
    /// <summary>The kind of a local or network folder.</summary>
    public const string FileSystemKind = "fs";

    /// <summary>A folder on a file system.</summary>
    public static StorageLocation FileSystem(string path) => new(FileSystemKind, path);

    /// <summary>Whether this is a folder on a file system.</summary>
    public bool IsFileSystem => string.Equals(Kind, FileSystemKind, StringComparison.Ordinal);
}

/// <summary>
/// Reads a location from its object form or from the plain path string older plan files used (a file system folder);
/// always writes the object form <c>{"kind","path"}</c> plus <c>connectionId</c> when there is one, whatever the
/// serializer's naming or null-handling options say.
/// </summary>
public sealed class StorageLocationJsonConverter : JsonConverter<StorageLocation>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override StorageLocation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return StorageLocation.FileSystem(reader.GetString()!);
            case JsonTokenType.StartObject:
                return ReadObject(ref reader);
            default:
                throw new JsonException("A storage location must be a path string or an object with \"kind\" and \"path\".");
        }
    }

    private static StorageLocation ReadObject(ref Utf8JsonReader reader)
    {
        string? kind = null, path = null, connectionId = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            if (name.Equals("kind", StringComparison.OrdinalIgnoreCase))
                kind = ReadString(ref reader, name);
            else if (name.Equals("path", StringComparison.OrdinalIgnoreCase))
                path = ReadString(ref reader, name);
            else if (name.Equals("connectionId", StringComparison.OrdinalIgnoreCase))
                connectionId = reader.TokenType == JsonTokenType.Null ? null : ReadString(ref reader, name);
            else
                reader.Skip();
        }
        if (kind is null || path is null)
            throw new JsonException("A storage location object needs \"kind\" and \"path\".");
        return new StorageLocation(kind, path, connectionId);
    }

    private static string ReadString(ref Utf8JsonReader reader, string name) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString()!
            : throw new JsonException($"The storage location's \"{name}\" must be a string.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StorageLocation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        writer.WriteString("path", value.Path);
        if (value.ConnectionId is not null)
            writer.WriteString("connectionId", value.ConnectionId);
        writer.WriteEndObject();
    }
}
