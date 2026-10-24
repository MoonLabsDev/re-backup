namespace ReBackup.Control;

public static class ControlErrors
{
    public const string AppNotRunning = "app_not_running";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string PlanNotFound = "plan_not_found";
    public const string VersionNotFound = "version_not_found";
    public const string VersionNotIndexed = "version_not_indexed";
    public const string ScanRunning = "scan_running";
    public const string PlanRunning = "plan_running";
    public const string SourceNotFolder = "source_not_folder";
    public const string ProposalUnknown = "proposal_unknown";
    public const string ProposalExpired = "proposal_expired";
    public const string ProposalStale = "proposal_stale";
    public const string InvalidPattern = "invalid_pattern";

    /// <summary>Not part of the spec's list: transport-level failures (unparsable line, handler crash).</summary>
    public const string BadRequest = "bad_request";
    public const string InternalError = "internal_error";

    public const string AppNotRunningMessage = "ReBackup is not running, or MCP control is disabled in Settings.";
}

/// <summary>Nothing listens on the pipe, or the connection broke.</summary>
public sealed class ControlUnavailableException(string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string Key => ControlErrors.AppNotRunning;
}

/// <summary>The peer speaks a different protocol version.</summary>
public sealed class ControlProtocolException(string message) : Exception(message)
{
    public string Key => ControlErrors.ProtocolMismatch;
}
