using System.Text.Json;
using System.Text.Json.Serialization;
using ReBackup.Shared.Json;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Plans;

/// <summary>How an incremental run decides that a file is unchanged.</summary>
public enum ChangeDetection
{
    /// <summary>Same size and modification time as in the previous version.</summary>
    Fast,
    /// <summary>Every file is read and hashed.</summary>
    Safe,
}

public sealed class BackupPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public StorageLocation Source { get; set; } = StorageLocation.FileSystem("");
    public StorageLocation Target { get; set; } = StorageLocation.FileSystem("");
    public bool Enabled { get; set; } = true;
    public bool FreeSpaceByRetention { get; set; }

    /// <summary>Whether versions share unchanged files through a content-addressed pool in the target.</summary>
    public bool Incremental { get; set; }

    /// <summary>How an incremental run finds changed files. Ignored for full plans.</summary>
    public ChangeDetection ChangeDetection { get; set; } = ChangeDetection.Fast;

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
