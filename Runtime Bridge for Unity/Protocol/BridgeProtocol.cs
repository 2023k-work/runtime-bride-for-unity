using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuntimeBridge.Unity.Protocol;

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
}

public sealed class BridgeRequest
{
    [JsonPropertyName("type")] public string Type { get; init; } = "request";
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("operation")] public string Operation { get; init; } = string.Empty;
    [JsonPropertyName("session")] public string? Session { get; init; }
    [JsonPropertyName("command")] public string? Command { get; init; }
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
}

public sealed class BridgeResponse
{
    [JsonPropertyName("type")] public string Type { get; init; } = "response";
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("operation")] public string Operation { get; init; } = string.Empty;
    [JsonPropertyName("session")] public string? Session { get; init; }
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }
    [JsonPropertyName("error")] public BridgeError? Error { get; init; }
}

public sealed class BridgeError
{
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

public sealed class BridgeProtocolException(string message) : Exception(message);

public sealed class BridgeRemoteException(string code, string message)
    : Exception($"Unity bridge returned {code}: {message}")
{
    public string Code { get; } = code;
}
