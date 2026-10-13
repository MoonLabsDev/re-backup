namespace ReBackup.Core.Config;

public sealed record ConfigPaths(string Root)
{
    public string PlansDirectory => Path.Combine(Root, "plans");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The S3 accounts and connections the plans refer to (secrets DPAPI-bound to the Windows account).</summary>
    public string ConnectionsFile => Path.Combine(Root, "connections.json");

    /// <summary>The Google accounts (service-account keys, DPAPI-bound) and Drive connections the plans refer to.</summary>
    public string DriveConnectionsFile => Path.Combine(Root, "drive-connections.json");

    public string LogFileFor(string planId) => Path.Combine(LogsDirectory, planId + ".jsonl");
}
