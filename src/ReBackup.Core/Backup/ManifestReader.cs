using System.Text.Json;
using ReBackup.Core.Localization;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// The fields of a manifest that stand in front of its file list. The totals are null in manifests written
/// before those fields existed.
/// </summary>
public sealed record ManifestHeader(string PlanId, string PlanName, DateTime CreatedUtc, int? FileCount, long? TotalBytes);

/// <summary>
/// Reads <c>re-manifest.json</c> files from a storage, without their (possibly huge) file list where that is possible.
/// Read streams are never assumed to be seekable.
/// </summary>
public static class ManifestReader
{
    private const int HeaderBufferSize = 64 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Reads the plan id, the name, the time and the totals of a manifest. The stream is read forward only: a window at
    /// the start of the file usually holds the header; when it does not, the manifest is opened a second time and read
    /// in full.
    /// </summary>
    /// <exception cref="StorageException">The file is missing (<see cref="StorageNotFoundException"/>) or cannot be read.</exception>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static async Task<ManifestHeader> ReadHeaderAsync(IStorage storage, string manifestPath, CancellationToken ct = default)
    {
        byte[] buffer;
        int length;
        var stream = await storage.OpenReadAsync(manifestPath, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            buffer = new byte[HeaderBufferSize];
            length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        }

        var endOfFile = length < buffer.Length;
        if (TryReadHeader(buffer.AsSpan(0, length), isFinalBlock: endOfFile) is { } header)
            return header;

        // Unusual layout or a header that does not fit the window: read everything (again, unless the window held it all).
        BackupManifest manifest;
        if (endOfFile)
        {
            manifest = ReadManifest(new MemoryStream(buffer, 0, length, writable: false));
        }
        else
        {
            var again = await storage.OpenReadAsync(manifestPath, ct).ConfigureAwait(false);
            await using (again.ConfigureAwait(false))
                manifest = await ReadManifestAsync(again, ct).ConfigureAwait(false);
        }
        return new ManifestHeader(manifest.PlanId, manifest.PlanName, manifest.CreatedUtc,
            manifest.FileCount ?? manifest.Files.Count, manifest.TotalBytes ?? SumSizes(manifest));
    }

    /// <summary>Counts and sums the file list; for manifests without totals in their header.</summary>
    /// <exception cref="StorageException">The file is missing (<see cref="StorageNotFoundException"/>) or cannot be read.</exception>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static async Task<(int FileCount, long TotalBytes)> ReadTotalsAsync(IStorage storage, string manifestPath,
        CancellationToken ct = default)
    {
        var stream = await storage.OpenReadAsync(manifestPath, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var manifest = await ReadManifestAsync(stream, ct).ConfigureAwait(false);
            return (manifest.Files.Count, SumSizes(manifest));
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(Stream stream, CancellationToken ct) =>
        Normalize(await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonDefaults.Options, ct).ConfigureAwait(false));

    private static BackupManifest ReadManifest(Stream stream) =>
        Normalize(JsonSerializer.Deserialize<BackupManifest>(stream, JsonDefaults.Options));

    /// <summary>Fills what a format 1 manifest (or a damaged one) leaves out.</summary>
    private static BackupManifest Normalize(BackupManifest? manifest)
    {
        if (manifest is null)
            throw new JsonException(CoreTexts.English("core.manifest.empty"));
        manifest.PlanId ??= "";
        manifest.PlanName ??= "";
        manifest.Files ??= [];
        manifest.Directories ??= [];
        return manifest;
    }

    private static long SumSizes(BackupManifest manifest)
    {
        long total = 0;
        foreach (var file in manifest.Files)
            total += file?.Size ?? 0;
        return total;
    }

    /// <summary>Null when the header does not end within <paramref name="json"/> or has an unexpected shape.</summary>
    private static ManifestHeader? TryReadHeader(ReadOnlySpan<byte> json, bool isFinalBlock)
    {
        if (json.StartsWith(Utf8Bom))
            json = json[Utf8Bom.Length..];

        string? planId = null;
        var planName = "";
        DateTime createdUtc = default;
        int? fileCount = null;
        long? totalBytes = null;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (true)
            {
                if (!reader.Read())
                    return null;   // the header does not end within the buffer
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;

                var name = reader.GetString();
                if (Is(name, "files"))
                    break;
                if (!reader.Read())
                    return null;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                        return null;
                    continue;
                }

                if (Is(name, "planId") && reader.TokenType == JsonTokenType.String)
                    planId = reader.GetString();
                else if (Is(name, "planName") && reader.TokenType == JsonTokenType.String)
                    planName = reader.GetString() ?? "";
                else if (Is(name, "createdUtc") && reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var created))
                    createdUtc = created;
                else if (Is(name, "fileCount") && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var count))
                    fileCount = count;
                else if (Is(name, "totalBytes") && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var bytes))
                    totalBytes = bytes;
            }
        }
        catch (JsonException)
        {
            return null;   // the full read reports what is wrong
        }

        return planId is null ? null : new ManifestHeader(planId, planName, createdUtc, fileCount, totalBytes);
    }

    private static bool Is(string? name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
