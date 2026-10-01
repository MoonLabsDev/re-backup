using System.Text.Json;
using ReBackup.Core.Json;
using ReBackup.Core.Localization;

namespace ReBackup.Core.Backup;

/// <summary>
/// The fields of a manifest that stand in front of its file list. The totals are null in manifests written
/// before those fields existed.
/// </summary>
public sealed record ManifestHeader(string PlanId, string PlanName, DateTime CreatedUtc, int? FileCount, long? TotalBytes);

/// <summary>Reads <c>re-manifest.json</c> files, without their (possibly huge) file list where that is possible.</summary>
public static class ManifestReader
{
    private const int HeaderBufferSize = 64 * 1024;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Reads the plan id, the name, the time and the totals of a manifest.</summary>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static ManifestHeader ReadHeader(string manifestPath)
    {
        using var stream = Open(manifestPath);
        var buffer = new byte[(int)Math.Min(HeaderBufferSize, Math.Max(1L, stream.Length))];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (TryReadHeader(buffer.AsSpan(0, length), isFinalBlock: length >= stream.Length) is { } header)
            return header;

        // Unusual layout or a header that does not fit the buffer: read everything.
        stream.Position = 0;
        var manifest = ReadManifest(stream);
        return new ManifestHeader(manifest.PlanId, manifest.PlanName, manifest.CreatedUtc,
            manifest.FileCount ?? manifest.Files.Count, manifest.TotalBytes ?? SumSizes(manifest));
    }

    /// <summary>Counts and sums the file list; for manifests without totals in their header.</summary>
    public static (int FileCount, long TotalBytes) ReadTotals(string manifestPath)
    {
        using var stream = Open(manifestPath);
        var manifest = ReadManifest(stream);
        return (manifest.Files.Count, SumSizes(manifest));
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static BackupManifest ReadManifest(Stream stream)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonDefaults.Options)
                       ?? throw new JsonException(CoreTexts.English("core.manifest.empty"));
        manifest.PlanId ??= "";
        manifest.PlanName ??= "";
        manifest.Files ??= [];
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
