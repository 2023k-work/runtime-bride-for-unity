using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuntimeBridge.Unity.Protocol;

namespace RuntimeBridge.Unity.Scenarios;

public sealed class RuntimeScenarioRunner(RuntimeBridgeService bridge)
{
    public ScenarioValidationResult Validate(string path)
    {
        var (result, _) = ScenarioValidator.ValidateFile(Path.GetFullPath(path));
        return result;
    }

    public IReadOnlyList<string> List(string path) => ScenarioValidator.ListFiles(path);

    public async Task<ScenarioBatchResult> RunPathAsync(string path, ScenarioRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var files = ScenarioValidator.ListFiles(path);
        if (files.Count == 0) throw new ScenarioDefinitionException($"No scenario JSON files were found under '{path}'.");
        var results = new List<ScenarioRunResult>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunFileAsync(file, options ?? new(), cancellationToken).ConfigureAwait(false));
        }
        return new() { Status = Aggregate(results.Select(item => item.Status)), Results = results };
    }

    private async Task<ScenarioRunResult> RunFileAsync(string path, ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var (validation, scenario) = ScenarioValidator.ValidateFile(fullPath);
        var scenarioName = scenario?.Name ?? Path.GetFileNameWithoutExtension(fullPath);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + "-" + Guid.NewGuid().ToString("N")[..8];
        var root = Path.GetFullPath(options.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "TestResults"));
        var evidenceDirectory = Path.Combine(root, Sanitize(scenarioName), runId);
        Directory.CreateDirectory(evidenceDirectory);
        var report = new ScenarioRunResult
        {
            Scenario = scenarioName,
            Version = scenario?.Version ?? 0,
            Status = ScenarioStatus.Error,
            RunId = runId,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O"),
            EvidenceDirectory = evidenceDirectory,
            GitCommit = options.GitCommit ?? scenario?.Metadata.GitCommit ?? GitMetadata.TryGetCommit()
        };

        await using var evidence = ScenarioEvidenceWriter.Create(evidenceDirectory);
        await evidence.HostAsync($"scenario={scenarioName} path={fullPath} runId={runId}", cancellationToken).ConfigureAwait(false);
        if (!validation.Valid || scenario is null)
        {
            report.Errors.AddRange(validation.Errors);
            report.EndedUtc = DateTimeOffset.UtcNow.ToString("O");
            await evidence.HostAsync($"validation failed: {string.Join(" | ", report.Errors)}", cancellationToken).ConfigureAwait(false);
            await WriteResultAsync(report).ConfigureAwait(false);
            return report;
        }

        report.Status = ScenarioStatus.Pass;

        foreach (var instance in scenario.Instances)
            report.BuildArtifacts[instance.Id] = options.BuildArtifact ?? instance.Executable ?? scenario.Metadata.BuildArtifact;

        var instances = new Dictionary<string, InstanceContext>(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        try
        {
            foreach (var spec in scenario.Instances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = PrepareInstance(spec);
                instances.Add(spec.Id, context);
                await WaitReadyAsync(context, cancellationToken).ConfigureAwait(false);
                await evidence.HostAsync($"instance={spec.Id} session={context.Session ?? ""} port={context.Port} managed={context.Managed}", cancellationToken).ConfigureAwait(false);
            }

            var phases = new[]
            {
                (Name: "preconditions", Steps: scenario.Preconditions),
                (Name: "setup", Steps: scenario.Setup),
                (Name: "actions", Steps: scenario.Actions),
                (Name: "assertions", Steps: scenario.Assertions)
            };
            foreach (var phase in phases)
            {
                if (!await RunPhaseAsync(phase.Name, phase.Steps, instances, values, report, evidence, cancellationToken).ConfigureAwait(false))
                    break;
            }
            if (report.Steps.All(step => step.Status == ScenarioStatus.Pass)) report.Status = ScenarioStatus.Pass;
        }
        catch (ScenarioBlockedException ex)
        {
            report.Status = ScenarioStatus.Blocked;
            report.Errors.Add(ex.Message);
            await evidence.HostAsync($"blocked: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report.Status = ScenarioStatus.Error;
            report.Errors.Add("Scenario execution was cancelled.");
        }
        catch (Exception ex)
        {
            report.Status = ScenarioStatus.Error;
            report.Errors.Add(ex.Message);
            await evidence.HostAsync($"runner error: {ex}", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await RunPhaseAsync("cleanup", scenario.Cleanup, instances, values, report, evidence, CancellationToken.None, continueAfterFailure: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                report.Status = ScenarioStatus.Error;
                report.Errors.Add($"cleanup error: {ex.Message}");
            }

            foreach (var context in instances.Values.Reverse())
            {
                if (!context.Managed || context.Session is null) continue;
                try
                {
                    await bridge.StopPlayerAsync(context.Session, context.StopTimeoutMs, CancellationToken.None).ConfigureAwait(false);
                    await evidence.HostAsync($"instance={context.Spec.Id} stopped", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    report.Status = ScenarioStatus.Error;
                    report.Errors.Add($"instance '{context.Spec.Id}' cleanup failed: {ex.Message}");
                    await evidence.HostAsync($"instance={context.Spec.Id} stop failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
                }
            }

            CopyPlayerLogs(instances, evidenceDirectory, report);
            if (report.Errors.Count > 0 && report.Status == ScenarioStatus.Pass) report.Status = ScenarioStatus.Error;
            report.EndedUtc = DateTimeOffset.UtcNow.ToString("O");
            await evidence.HostAsync($"status={report.Status}", CancellationToken.None).ConfigureAwait(false);
            await WriteResultAsync(report).ConfigureAwait(false);
        }
        return report;
    }

    private InstanceContext PrepareInstance(ScenarioInstance spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Executable))
        {
            var session = bridge.StartPlayer(spec.Executable, spec.Port ?? PlayerProcess.DefaultPort, spec.Session, spec.LogDirectory);
            return new(spec, session.Session, session.Port, session.LogPath, true);
        }

        var port = spec.Port ?? PlayerProcess.DefaultPort;
        return new(spec, spec.Session, port, null, false);
    }

    private async Task WaitReadyAsync(InstanceContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Managed && context.Session is not null)
            {
                await bridge.WaitReadyAsync(context.Session, 10_000, cancellationToken).ConfigureAwait(false);
                return;
            }
            await bridge.ConnectAsync(context.Session ?? string.Empty, context.Port, 3_000, cancellationToken).ConfigureAwait(false);
            await bridge.WaitReadyAtAsync("127.0.0.1", context.Port, context.Session, 10_000, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or SocketException or BridgeProtocolException or BridgeRemoteException)
        {
            throw new ScenarioBlockedException(
                $"Attached instance '{context.Spec.Id}' was not ready; the Runner does not own its process. {ex.Message}", ex);
        }
    }

    private async Task<bool> RunPhaseAsync(string phase, IReadOnlyList<ScenarioStep> steps,
        IReadOnlyDictionary<string, InstanceContext> instances, Dictionary<string, JsonNode?> values,
        ScenarioRunResult report, ScenarioEvidenceWriter evidence, CancellationToken cancellationToken,
        bool continueAfterFailure = false)
    {
        var succeeded = true;
        foreach (var step in steps)
        {
            var result = await RunStepAsync(phase, step, instances, values, evidence, cancellationToken).ConfigureAwait(false);
            report.Steps.Add(result);
            values[step.Id] = result.Output?.DeepClone();
            if (result.Status != ScenarioStatus.Pass)
            {
                succeeded = false;
                report.Status = Prefer(report.Status, result.Status);
                if (!string.IsNullOrWhiteSpace(result.Message)) report.Errors.Add($"{phase}/{step.Id}: {result.Message}");
                if (!continueAfterFailure) break;
            }
        }
        return succeeded;
    }

    private async Task<ScenarioStepEvidence> RunStepAsync(string phase, ScenarioStep step,
        IReadOnlyDictionary<string, InstanceContext> instances, Dictionary<string, JsonNode?> values,
        ScenarioEvidenceWriter evidence, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var result = new ScenarioStepEvidence
        {
            Phase = phase, Id = step.Id, Kind = step.Kind, Instance = step.Instance,
            Status = ScenarioStatus.Error, StartedUtc = started.ToString("O")
        };
        try
        {
            var kind = step.Kind.ToLowerInvariant();
            switch (kind)
            {
                case "command":
                    result.Output = await ExecuteCommandAsync(step, instances, evidence, cancellationToken).ConfigureAwait(false);
                    result.Status = ScenarioStatus.Pass;
                    break;
                case "probe":
                    result.Output = await ExecuteProbeAsync(step, instances, evidence, cancellationToken, 1).ConfigureAwait(false);
                    result.Status = ScenarioStatus.Pass;
                    break;
                case "wait":
                    result.Output = await ExecuteWaitAsync(step, instances, values, evidence, cancellationToken).ConfigureAwait(false);
                    result.Status = ScenarioStatus.Pass;
                    break;
                case "assert":
                    var actual = ScenarioAssertions.Resolve(values, step.Actual!);
                    var expected = step.ExpectedFrom is null ? step.Value?.DeepClone() : ScenarioAssertions.Resolve(values, step.ExpectedFrom);
                    var assertion = ScenarioAssertions.Evaluate(actual, step.Operator!, expected);
                    result.Actual = assertion.Actual;
                    result.Expected = assertion.Expected;
                    result.Output = new JsonObject { ["passed"] = assertion.Passed, ["message"] = assertion.Message };
                    result.Status = assertion.Passed ? ScenarioStatus.Pass : ScenarioStatus.Fail;
                    result.Message = assertion.Message;
                    break;
                case "manual":
                    result.Status = ScenarioStatus.ManualRequired;
                    result.Message = step.Reason;
                    break;
                default:
                    throw new ScenarioDefinitionException($"Unsupported step kind '{step.Kind}'.");
            }
        }
        catch (BridgeRemoteException ex)
        {
            result.ErrorCode = ex.Code;
            result.Message = ex.Message;
            result.Output = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message } };
            result.Status = string.Equals(step.ExpectedErrorCode, ex.Code, StringComparison.Ordinal) ? ScenarioStatus.Pass : ScenarioStatus.Fail;
        }
        catch (TimeoutException ex)
        {
            result.ErrorCode = "TIMEOUT_UNKNOWN";
            result.Message = ex.Message;
            result.Status = ScenarioStatus.Error;
        }
        catch (SocketException ex)
        {
            result.ErrorCode = "CONNECTION";
            result.Message = ex.Message;
            result.Status = ScenarioStatus.Error;
        }
        catch (ScenarioDefinitionException ex)
        {
            result.ErrorCode = "SCENARIO_DEFINITION";
            result.Message = ex.Message;
            result.Status = ScenarioStatus.Error;
        }
        catch (Exception ex) when (ex is BridgeProtocolException or IOException or JsonException)
        {
            result.ErrorCode = "TOOL_ERROR";
            result.Message = ex.Message;
            result.Status = ScenarioStatus.Error;
        }
        finally
        {
            var ended = DateTimeOffset.UtcNow;
            result.EndedUtc = ended.ToString("O");
            result.DurationMs = Math.Max(0, (long)(ended - started).TotalMilliseconds);
        }
        return result;
    }

    private async Task<JsonNode?> ExecuteCommandAsync(ScenarioStep step, IReadOnlyDictionary<string, InstanceContext> instances,
        ScenarioEvidenceWriter evidence, CancellationToken cancellationToken)
    {
        var context = GetInstance(step, instances);
        try
        {
            var response = await bridge.SendCommandAsync(step.Command!, step.Payload?.ToJsonString(), context.Session, context.Port, step.TimeoutMs, cancellationToken).ConfigureAwait(false);
            var output = ResponseOutput(response);
            await evidence.CommandAsync(new { utc = DateTimeOffset.UtcNow, step = step.Id, instance = context.Spec.Id, command = step.Command, payload = step.Payload, output }, cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex)
        {
            await evidence.CommandAsync(new { utc = DateTimeOffset.UtcNow, step = step.Id, instance = context.Spec.Id, command = step.Command, payload = step.Payload, error = ErrorOutput(ex) }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonNode?> ExecuteProbeAsync(ScenarioStep step, IReadOnlyDictionary<string, InstanceContext> instances,
        ScenarioEvidenceWriter evidence, CancellationToken cancellationToken, int attempt)
    {
        var context = GetInstance(step, instances);
        var payload = new JsonObject
        {
            ["probe"] = step.Probe,
            ["player"] = step.Player,
            ["args"] = step.Payload?.DeepClone()
        };
        try
        {
            var response = await bridge.SendCommandAsync(step.ProbeCommand ?? "runtime.probe", payload.ToJsonString(), context.Session, context.Port, step.TimeoutMs, cancellationToken).ConfigureAwait(false);
            var output = ResponseOutput(response);
            await evidence.ProbeAsync(new { utc = DateTimeOffset.UtcNow, step = step.Id, attempt, instance = context.Spec.Id, probe = step.Probe, player = step.Player, output }, cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch (Exception ex)
        {
            await evidence.ProbeAsync(new { utc = DateTimeOffset.UtcNow, step = step.Id, attempt, instance = context.Spec.Id, probe = step.Probe, player = step.Player, error = ErrorOutput(ex) }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonNode?> ExecuteWaitAsync(ScenarioStep step, IReadOnlyDictionary<string, InstanceContext> instances,
        Dictionary<string, JsonNode?> values, ScenarioEvidenceWriter evidence, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(step.TimeoutMs);
        var attempt = 0;
        JsonNode? last = null;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            attempt++;
            var probeStep = new ScenarioStep
            {
                Id = step.Id, Kind = "probe", Instance = step.Instance, Probe = step.Probe,
                Player = step.Player, ProbeCommand = step.ProbeCommand, Payload = step.Payload, TimeoutMs = Math.Min(step.TimeoutMs, 3_000)
            };
            last = await ExecuteProbeAsync(probeStep, instances, evidence, cancellationToken, attempt).ConfigureAwait(false);
            var actual = ScenarioAssertions.ResolvePath(last, step.Condition!.Path);
            var expected = step.Condition.ExpectedFrom is null ? step.Condition.Value?.DeepClone() : ScenarioAssertions.Resolve(values, step.Condition.ExpectedFrom);
            if (ScenarioAssertions.Evaluate(actual, step.Condition.Operator, expected).Passed)
                return new JsonObject { ["attempts"] = attempt, ["matched"] = true, ["value"] = last?.DeepClone() };
            await Task.Delay(step.PollMs, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Wait step '{step.Id}' did not satisfy its condition within {step.TimeoutMs} ms.");
    }

    private static InstanceContext GetInstance(ScenarioStep step, IReadOnlyDictionary<string, InstanceContext> instances) =>
        step.Instance is not null && instances.TryGetValue(step.Instance, out var context)
            ? context
            : throw new ScenarioDefinitionException($"Step '{step.Id}' references an unavailable instance.");

    private static JsonNode ResponseOutput(BridgeResponse response)
    {
        var output = new JsonObject { ["ok"] = response.Ok, ["operation"] = response.Operation, ["session"] = response.Session };
        if (response.Data is JsonElement data) output["data"] = JsonNode.Parse(data.GetRawText());
        if (response.Error is not null)
            output["error"] = new JsonObject { ["code"] = response.Error.Code, ["message"] = response.Error.Message };
        return output;
    }

    private static JsonNode ErrorOutput(Exception exception) => new JsonObject
    {
        ["code"] = exception is BridgeRemoteException remote ? remote.Code : exception is TimeoutException ? "TIMEOUT_UNKNOWN" : "TOOL_ERROR",
        ["message"] = exception.Message
    };

    private static void CopyPlayerLogs(IReadOnlyDictionary<string, InstanceContext> instances, string directory, ScenarioRunResult report)
    {
        var instanceDirectory = Path.Combine(directory, "instances");
        Directory.CreateDirectory(instanceDirectory);
        foreach (var context in instances.Values)
        {
            if (string.IsNullOrWhiteSpace(context.LogPath) || !File.Exists(context.LogPath)) continue;
            try
            {
                var target = Path.Combine(instanceDirectory, Sanitize(context.Spec.Id) + ".log");
                File.Copy(context.LogPath, target, overwrite: true);
                if (context.Spec.Id.Equals("client", StringComparison.OrdinalIgnoreCase))
                    File.Copy(context.LogPath, Path.Combine(directory, "client.log"), overwrite: true);
            }
            catch (IOException ex) { report.Errors.Add($"Could not copy log for '{context.Spec.Id}': {ex.Message}"); }
        }
    }

    private static async Task WriteResultAsync(ScenarioRunResult report)
    {
        var path = Path.Combine(report.EvidenceDirectory, "result.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, ScenarioJson.Options), new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
    }

    private static ScenarioStatus Prefer(ScenarioStatus current, ScenarioStatus next)
    {
        static int Rank(ScenarioStatus status) => status switch
        {
            ScenarioStatus.Error => 5,
            ScenarioStatus.Blocked => 4,
            ScenarioStatus.ManualRequired => 3,
            ScenarioStatus.Fail => 2,
            _ => 1
        };
        return Rank(next) > Rank(current) ? next : current;
    }

    private static ScenarioStatus Aggregate(IEnumerable<ScenarioStatus> statuses)
    {
        var result = ScenarioStatus.Pass;
        foreach (var status in statuses) result = Prefer(result, status);
        return result;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed record InstanceContext(ScenarioInstance Spec, string? Session, int Port, string? LogPath, bool Managed)
    {
        public int StopTimeoutMs { get; init; } = 10_000;
    }
}

internal sealed class ScenarioBlockedException(string message, Exception? inner = null) : Exception(message, inner);

internal static class GitMetadata
{
    public static string? TryGetCommit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git", Arguments = "rev-parse HEAD", RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (process is null) return null;
            process.WaitForExit(2_000);
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch { return null; }
    }
}
