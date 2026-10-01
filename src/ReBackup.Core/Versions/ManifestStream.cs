using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;

namespace ReBackup.Core.Versions;

/// <summary>The fields of a manifest other than its file list.</summary>
public sealed record ManifestSummary(string PlanId, string PlanName, DateTime CreatedUtc, string Source);

/// <summary>
/// Reads a <c>re-manifest.json</c> entry by entry: only a small window of the file is in memory at any time, however
/// long the file list is.
/// </summary>
public static class ManifestStream
{
    /// <summary>Size of the read window; it grows only for a single token or entry that does not fit.</summary>
    public const int InitialBufferSize = 64 * 1024;

    /// <summary>Upper bound of the read window: a single token or entry larger than this is rejected.</summary>
    public const int MaxBufferSize = 64 * 1024 * 1024;

    private enum Phase { Start, Properties, Files, Done }

    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static ManifestSummary Read(string manifestPath, Action<ManifestFile> onFile,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        return Read(stream, onFile, cancellationToken);
    }

    /// <inheritdoc cref="Read(string, Action{ManifestFile}, CancellationToken)"/>
    public static ManifestSummary Read(Stream stream, Action<ManifestFile> onFile,
        CancellationToken cancellationToken = default)
    {
        var inCallback = false;
        try
        {
            return ReadCore(stream, f =>
            {
                inCallback = true;
                onFile(f);
                inCallback = false;
            }, cancellationToken);
        }
        catch (Exception ex) when (!inCallback && ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            // e.g. invalid UTF-8 in a string: Utf8JsonReader reports it other than as a JsonException.
            throw new JsonException(CoreTexts.English("core.manifest.unreadable", ("error", ex.Message)), ex);
        }
    }

    private static ManifestSummary ReadCore(Stream stream, Action<ManifestFile> onFile, CancellationToken cancellationToken)
    {
        var buffer = new byte[InitialBufferSize];
        var length = Fill(stream, buffer, 0, out var endOfStream);
        var start = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        var state = new JsonReaderState();
        var phase = Phase.Start;
        var fields = new Fields();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reader = new Utf8JsonReader(buffer.AsSpan(start, length - start), endOfStream, state);
            while (phase != Phase.Done && TryStep(ref reader, ref phase, fields, onFile))
            {
            }
            if (phase == Phase.Done)
                return new ManifestSummary(fields.PlanId, fields.PlanName, fields.CreatedUtc, fields.Source);
            if (endOfStream)
                throw new JsonException(CoreTexts.English("core.manifest.endsEarly"));

            // Keep what was not consumed, then read more behind it; grow only when nothing at all was consumed.
            var consumed = start + (int)reader.BytesConsumed;
            state = reader.CurrentState;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;
            start = 0;
            if (length == buffer.Length)
            {
                if (buffer.Length >= MaxBufferSize)
                    throw new JsonException(CoreTexts.English("core.manifest.valueTooLarge"));
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            length += Fill(stream, buffer, length, out endOfStream);
        }
    }

    private static int Fill(Stream stream, byte[] buffer, int offset, out bool endOfStream)
    {
        var wanted = buffer.Length - offset;
        var read = stream.ReadAtLeast(buffer.AsSpan(offset), wanted, throwOnEndOfStream: false);
        endOfStream = read < wanted;
        return read;
    }

    /// <summary>
    /// Reads one unit (the opening brace, one property, or one file entry) on a copy of the reader and takes it over
    /// only when the unit is complete in the buffer. False: more data is needed.
    /// </summary>
    private static bool TryStep(ref Utf8JsonReader reader, ref Phase phase, Fields fields, Action<ManifestFile> onFile)
    {
        var probe = reader;
        switch (phase)
        {
            case Phase.Start:
                if (!probe.Read())
                    return false;
                if (probe.TokenType != JsonTokenType.StartObject)
                    throw new JsonException(CoreTexts.English("core.manifest.notObject"));
                phase = Phase.Properties;
                break;

            case Phase.Properties:
            {
                if (!probe.Read())
                    return false;
                if (probe.TokenType == JsonTokenType.EndObject)
                {
                    phase = Phase.Done;
                    break;
                }
                var name = probe.GetString();
                if (!probe.Read())
                    return false;
                if (Is(name, "files") && probe.TokenType == JsonTokenType.StartArray)
                {
                    phase = Phase.Files;
                    break;
                }
                if (probe.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!probe.TrySkip())
                        return false;
                    break;
                }
                fields.Set(name, ref probe);
                break;
            }

            case Phase.Files:
            {
                if (!probe.Read())
                    return false;
                if (probe.TokenType == JsonTokenType.EndArray)
                {
                    phase = Phase.Properties;
                    break;
                }
                if (probe.TokenType == JsonTokenType.Null)
                    break;
                if (probe.TokenType != JsonTokenType.StartObject)
                    throw new JsonException(CoreTexts.English("core.manifest.entryNotObject"));
                var entry = probe;
                if (!probe.TrySkip())
                    return false;
                onFile(ReadFile(ref entry));   // the whole entry is in the buffer: reading it cannot run dry
                break;
            }
        }

        reader = probe;
        return true;
    }

    private static ManifestFile ReadFile(ref Utf8JsonReader reader)
    {
        string? path = null;
        long size = 0;
        DateTime mtime = default;
        var hash = "";
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (Is(name, "path") && reader.TokenType == JsonTokenType.String)
                path = reader.GetString();
            else if (Is(name, "size") && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value))
                size = value;
            else if (Is(name, "mtimeUtc") && reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var time))
                mtime = time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime();
            else if (Is(name, "hash") && reader.TokenType == JsonTokenType.String)
                hash = reader.GetString() ?? "";
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();
        }
        if (string.IsNullOrEmpty(path))
            throw new JsonException(CoreTexts.English("core.manifest.entryNoPath"));
        return new ManifestFile(path, size, mtime, hash);
    }

    private static bool Is(string? name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

    private sealed class Fields
    {
        public string PlanId = "";
        public string PlanName = "";
        public string Source = "";
        public DateTime CreatedUtc;

        public void Set(string? name, ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.String)
                return;
            if (Is(name, "planId"))
                PlanId = reader.GetString() ?? "";
            else if (Is(name, "planName"))
                PlanName = reader.GetString() ?? "";
            else if (Is(name, "source"))
                Source = reader.GetString() ?? "";
            else if (Is(name, "createdUtc") && reader.TryGetDateTime(out var created))
                CreatedUtc = created;
        }
    }
}
