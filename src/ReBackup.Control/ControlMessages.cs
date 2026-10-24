using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Control;

/// <summary>One request line: <c>{"id","op","args"}</c>.</summary>
public sealed record ControlRequest(string Id, string Op, JsonElement Args);

/// <summary>One response line: <c>{"id","ok","result"|"error"}</c>.</summary>
public sealed record ControlResponse(string Id, bool Ok, JsonElement? Result, ControlError? Error)
{
    public static ControlResponse Success(string id, object? result) =>
        new(id, true, JsonSerializer.SerializeToElement(result, ControlJson.Options), null);

    public static ControlResponse Failure(string id, string key, string message) =>
        new(id, false, null, new ControlError(key, message));
}

/// <summary>Stable error key plus an English message.</summary>
public sealed record ControlError(string Key, string Message);

/// <summary>First line the server sends on every connection.</summary>
public sealed record ControlHandshake(int Protocol, string AppVersion);

public interface IControlHandler
{
    Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct);
}

public static class ControlJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
