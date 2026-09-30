namespace ReBackup.Core.Config;

public sealed record ConfigPaths(string Root)
{
    public string PlansDirectory => Path.Combine(Root, "plans");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogFileFor(string planId) => Path.Combine(LogsDirectory, planId + ".jsonl");
}
