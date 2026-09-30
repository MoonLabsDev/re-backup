namespace ReBackup.Core.Plans;

/// <summary>The "ignore" section of a plan.</summary>
public sealed class IgnoreSettings
{
    /// <summary>Also apply the default patterns from the global settings.</summary>
    public bool UseGlobalDefaults { get; set; } = true;

    /// <summary>Honor <c>.backupignore</c> files found inside the source tree.</summary>
    public bool HonorNestedFiles { get; set; } = true;

    /// <summary>gitignore-style lines, relative to the source root. Comments and blank lines are allowed.</summary>
    public List<string> Patterns { get; set; } = [];
}
