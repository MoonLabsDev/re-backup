namespace ReBackup.Shared.Schedule;

/// <summary>
/// What started a backup run. <see cref="Mcp"/>: Claude, through the MCP control pipe. <see cref="Retry"/>: the automatic
/// retry of the files a run skipped.
/// </summary>
public enum RunTrigger { Manual, Scheduled, CatchUp, Mcp, Retry }
