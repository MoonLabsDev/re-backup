namespace ReBackup.Control;

/// <summary>The <c>op</c> names of the control requests; the bridge's tools send them, the app's control service answers them.</summary>
public static class ControlOps
{
    public const string ListPlans = "list_plans";
    public const string ListVersions = "list_versions";
    public const string GetIgnoreRules = "get_ignore_rules";
    public const string ExplainPath = "explain_path";
    public const string ListFiles = "list_files";
    public const string GetRunHistory = "get_run_history";
    public const string CompareVersions = "compare_versions";
    public const string CompareSourceWithVersion = "compare_source_with_version";
}
