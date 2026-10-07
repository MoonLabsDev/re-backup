using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Shared.Json;

public static class JsonDefaults
{
    /// <summary>Indented JSON for plan, settings and manifest files.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>Single-line JSON for JSON Lines files (the run log).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
