using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuntimeBridge.Unity.Scenarios;

public sealed class RuntimeScenario
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public ScenarioMetadata Metadata { get; set; } = new();
    public List<ScenarioInstance> Instances { get; set; } = new();
    public List<ScenarioStep> Preconditions { get; set; } = new();
    public List<ScenarioStep> Setup { get; set; } = new();
    public List<ScenarioStep> Actions { get; set; } = new();
    public List<ScenarioStep> Assertions { get; set; } = new();
    public List<ScenarioStep> Cleanup { get; set; } = new();
}

public sealed class ScenarioMetadata
{
    public string? Description { get; set; }
    public string? GitCommit { get; set; }
    public string? BuildArtifact { get; set; }
}

public sealed class ScenarioInstance
{
    public string Id { get; set; } = string.Empty;
    public string? Executable { get; set; }
    public string? Session { get; set; }
    public int? Port { get; set; }
    public string? LogDirectory { get; set; }
}

public sealed class ScenarioStep
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "command";
    public string? Instance { get; set; }
    public string? Command { get; set; }
    public JsonNode? Payload { get; set; }
    public string? Probe { get; set; }
    public string? Player { get; set; }
    public string? ProbeCommand { get; set; }
    public ScenarioCondition? Condition { get; set; }
    public ScenarioReference? Actual { get; set; }
    public string? Operator { get; set; }
    public JsonNode? Value { get; set; }
    public ScenarioReference? ExpectedFrom { get; set; }
    public string? Reason { get; set; }
    public string? ExpectedErrorCode { get; set; }
    public int TimeoutMs { get; set; } = 3_000;
    public int PollMs { get; set; } = 100;
}

public sealed class ScenarioCondition
{
    public string Path { get; set; } = string.Empty;
    public string Operator { get; set; } = "equals";
    public JsonNode? Value { get; set; }
    public ScenarioReference? ExpectedFrom { get; set; }
}

public sealed class ScenarioReference
{
    public string Step { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class ScenarioValidationResult
{
    public bool Ok => Valid;
    public bool Valid => Errors.Count == 0;
    public string? Path { get; set; }
    public string? ScenarioName { get; set; }
    public List<string> Errors { get; } = new();

    public static ScenarioValidationResult Success(string path, RuntimeScenario scenario) =>
        new() { Path = path, ScenarioName = scenario.Name };
}

public sealed class ScenarioRunOptions
{
    public string? OutputDirectory { get; init; }
    public string? GitCommit { get; init; }
    public string? BuildArtifact { get; init; }
}

public sealed class ScenarioBatchResult
{
    public bool Ok => Status == ScenarioStatus.Pass;
    public ScenarioStatus Status { get; init; }
    public List<ScenarioRunResult> Results { get; init; } = new();
}

public sealed class ScenarioRunResult
{
    public string Scenario { get; init; } = string.Empty;
    public int Version { get; init; }
    public ScenarioStatus Status { get; set; }
    public string RunId { get; init; } = string.Empty;
    public string StartedUtc { get; init; } = string.Empty;
    public string EndedUtc { get; set; } = string.Empty;
    public string EvidenceDirectory { get; init; } = string.Empty;
    public string? GitCommit { get; init; }
    public Dictionary<string, string?> BuildArtifacts { get; init; } = new();
    public List<ScenarioStepEvidence> Steps { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

public sealed class ScenarioStepEvidence
{
    public string Phase { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? Instance { get; init; }
    public ScenarioStatus Status { get; set; }
    public string StartedUtc { get; init; } = string.Empty;
    public string EndedUtc { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public JsonNode? Expected { get; set; }
    public JsonNode? Actual { get; set; }
    public JsonNode? Output { get; set; }
}

[JsonConverter(typeof(ScenarioStatusJsonConverter))]
public enum ScenarioStatus
{
    Pass,
    Fail,
    Blocked,
    Error,
    ManualRequired
}

public sealed class ScenarioStatusJsonConverter : JsonConverter<ScenarioStatus>
{
    public override ScenarioStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.Parse<ScenarioStatus>(reader.GetString() ?? string.Empty, ignoreCase: true);

    public override void Write(Utf8JsonWriter writer, ScenarioStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToUpperInvariant());
}

public sealed class ScenarioDefinitionException(string message) : Exception(message);
