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
/// <param name="notSent">
/// The request provably never reached the app (the connection was found broken before or while writing it), so sending
/// it again on a new connection cannot carry it out twice.
/// </param>
public sealed class ControlUnavailableException(string message, Exception? inner = null, bool notSent = false)
    : IOException(message, inner)
{
    public string Key => ControlErrors.AppNotRunning;

    /// <summary>True when the request was never sent: it may be sent again on a new connection, even a write.</summary>
    public bool NotSent { get; } = notSent;
}

/// <summary>The peer speaks a different protocol version, or runs under another identity (elevation).</summary>
/// <param name="identityMismatch">
/// The pipe belongs to a different identity than this process (one runs as administrator), not another version.
/// </param>
public sealed class ControlProtocolException(string message, Exception? inner = null, bool identityMismatch = false)
    : Exception(message, inner)
{
    public string Key => ControlErrors.ProtocolMismatch;

    /// <summary>True when the cause is the owner check (elevation), not the protocol version.</summary>
    public bool IdentityMismatch { get; } = identityMismatch;
}
