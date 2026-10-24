namespace ReBackup.Control;

/// <summary>Sends one control request and returns the app's answer; <see cref="ControlClient"/> over the pipe.</summary>
public interface IControlChannel
{
    /// <exception cref="ControlUnavailableException">The app is not reachable, or the connection broke.</exception>
    /// <exception cref="ControlProtocolException">The app answered in a way this side does not understand.</exception>
    Task<ControlResponse> SendAsync(string op, object? args, CancellationToken ct);
}
