using System.Text.Json;
using System.Text.Json.Serialization;
using ReBackup.Shared.Json;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Plans;

public sealed class BackupPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool FreeSpaceByRetention { get; set; }

    public IgnoreSettings Ignore { get; set; } = new();

    private List<RetentionRule> _retention = [];

    /// <summary>Which old versions to keep. Empty means: keep everything.</summary>
    public List<RetentionRule> Retention
    {
        get => _retention;
        set => _retention = value ?? [];
    }

    private List<ScheduleTrigger> _triggers = [];

    /// <summary>When the plan runs by itself. Empty means: only when started by hand.</summary>
    public List<ScheduleTrigger> Triggers
    {
        get => _triggers;
        set => _triggers = value ?? [];
    }

    /// <summary>Plan sections not modelled by this version survive a load/save round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public BackupPlan Clone() =>
        JsonSerializer.Deserialize<BackupPlan>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
}
