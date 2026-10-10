namespace ReBackup.Core.Config;

public sealed record ConfigPaths(string Root)
{
    public string PlansDirectory => Path.Combine(Root, "plans");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The S3 accounts and connections the plans refer to (secrets DPAPI-bound to the Windows account).</summary>
    public string ConnectionsFile => Path.Combine(Root, "connections.json");

    public string LogFileFor(string planId) => Path.Combine(LogsDirectory, planId + ".jsonl");
}
