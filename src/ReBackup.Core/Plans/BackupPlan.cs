using System.Text.Json;
using System.Text.Json.Serialization;
using ReBackup.Core.Json;

namespace ReBackup.Core.Plans;

public sealed class BackupPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool FreeSpaceByRetention { get; set; }

    /// <summary>Plan sections not yet modelled by this version (triggers, ignore, retention) survive a load/save round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public BackupPlan Clone() =>
        JsonSerializer.Deserialize<BackupPlan>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
}
