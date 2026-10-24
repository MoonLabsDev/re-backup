namespace ReBackup.Shared.Schedule;

/// <summary>What started a backup run. <see cref="Mcp"/>: Claude, through the MCP control pipe.</summary>
public enum RunTrigger { Manual, Scheduled, CatchUp, Mcp }
