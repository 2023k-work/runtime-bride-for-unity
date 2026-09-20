using System.ComponentModel;
using ModelContextProtocol.Server;
using RuntimeBridge.Unity.Scenarios;

namespace RuntimeBridge.Unity.Tools;

[McpServerToolType]
public sealed class RuntimeBridgeTools(RuntimeBridgeService bridge, RuntimeScenarioRunner scenarios)
{
    [McpServerTool(Name = "unity_player_start")]
    [Description("Starts a built Unity Player with the local runtime bridge and returns tracked process/session metadata.")]
    public object StartPlayer([Description("Absolute path to the built Unity Player executable.")] string executablePath,
        [Description("Loopback TCP port used by this Player.")] int port = PlayerProcess.DefaultPort,
        [Description("Optional caller-provided session identity.")] string? session = null) =>
        new { ok = true, process = bridge.StartPlayer(executablePath, port, session).ToPublicMetadata() };

    [McpServerTool(Name = "unity_player_wait_ready")]
    [Description("Waits until a tracked Unity Player reports runtime readiness. A timeout means the outcome is unknown.")]
    public Task<object> WaitReady(string session, int timeoutMilliseconds = 10_000, CancellationToken cancellationToken = default) =>
        bridge.WaitReadyAsync(session, timeoutMilliseconds, cancellationToken);

    [McpServerTool(Name = "unity_player_status")]
    [Description("Returns verified process identity plus the authoritative runtime status when reachable.")]
    public Task<object> Status(string session, int timeoutMilliseconds = 3_000, CancellationToken cancellationToken = default) =>
        bridge.GetPlayerStatusAsync(session, timeoutMilliseconds, cancellationToken);

    [McpServerTool(Name = "unity_player_command")]
    [Description("Invokes a command registered inside a Unity Player. Timeout does not prove rollback or failure.")]
    public Task<Protocol.BridgeResponse> Command(string command, string? payloadJson = null, string? session = null,
        int port = PlayerProcess.DefaultPort, int timeoutMilliseconds = 3_000, CancellationToken cancellationToken = default) =>
        bridge.SendCommandAsync(command, payloadJson, session, port, timeoutMilliseconds, cancellationToken);

    [McpServerTool(Name = "unity_player_stop")]
    [Description("Requests graceful shutdown of a tracked Player after verifying PID, executable path, and process start time. Never kills a mismatched process.")]
    public Task<object> StopPlayer(string session, int timeoutMilliseconds = 10_000, CancellationToken cancellationToken = default) =>
        bridge.StopPlayerAsync(session, timeoutMilliseconds, cancellationToken);

    [McpServerTool(Name = "unity_scenario_validate")]
    [Description("Validates a Runtime Scenario JSON file without starting any Unity Player.")]
    public object ValidateScenario([Description("Absolute or working-directory-relative scenario JSON path.")] string path)
    {
        return scenarios.Validate(path);
    }

    [McpServerTool(Name = "unity_scenario_run")]
    [Description("Runs one Runtime Scenario JSON file or a directory of scenarios and writes repeatable evidence.")]
    public Task<ScenarioBatchResult> RunScenario(
        [Description("Scenario JSON file or directory.")] string path,
        [Description("Evidence output directory. Defaults to TestResults.")] string? outputDirectory = null,
        [Description("Optional source Git commit recorded in result.json.")] string? gitCommit = null,
        [Description("Optional build artifact recorded in result.json.")] string? buildArtifact = null,
        CancellationToken cancellationToken = default)
    {
        return scenarios.RunPathAsync(path, new ScenarioRunOptions
        {
            OutputDirectory = outputDirectory,
            GitCommit = gitCommit,
            BuildArtifact = buildArtifact
        }, cancellationToken);
    }

    [McpServerTool(Name = "unity_scenario_list")]
    [Description("Lists Runtime Scenario JSON files without starting any Unity Player.")]
    public object ListScenarios([Description("Scenario directory.")] string? path = null)
    {
        var directory = string.IsNullOrWhiteSpace(path)
            ? (Directory.Exists(Path.Combine(Environment.CurrentDirectory, "Runtime Bridge for Unity", "Scenarios"))
                ? Path.Combine(Environment.CurrentDirectory, "Runtime Bridge for Unity", "Scenarios")
                : Path.Combine(Environment.CurrentDirectory, "Scenarios"))
            : path;
        return new { ok = true, path = Path.GetFullPath(directory), scenarios = scenarios.List(directory) };
    }
}
