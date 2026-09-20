using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuntimeBridge.Unity.Scenarios;

public static class ScenarioValidator
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "probe", "wait", "assert", "manual"
    };

    public static (ScenarioValidationResult Result, RuntimeScenario? Scenario) ValidateFile(string path)
    {
        var result = new ScenarioValidationResult { Path = path };
        try
        {
            var json = File.ReadAllText(path);
            var scenario = JsonSerializer.Deserialize<RuntimeScenario>(json, ScenarioJson.Options)
                ?? throw new ScenarioDefinitionException("Scenario document is empty.");
            result.ScenarioName = scenario.Name;
            Validate(scenario, result);
            return (result, scenario);
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            result.Errors.Add($"Cannot read scenario: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Errors.Add($"Cannot read scenario: {ex.Message}");
        }
        return (result, null);
    }

    public static IReadOnlyList<string> ListFiles(string path)
    {
        if (File.Exists(path)) return new[] { Path.GetFullPath(path) };
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Scenario path was not found: {path}");
        return Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).Equals("runtime-scenario-v1.schema.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToArray();
    }

    private static void Validate(RuntimeScenario scenario, ScenarioValidationResult result)
    {
        if (scenario.Version != 1) result.Errors.Add("version must be 1.");
        if (string.IsNullOrWhiteSpace(scenario.Name)) result.Errors.Add("name is required.");
        if (scenario.Instances.Count == 0) result.Errors.Add("at least one instance is required.");

        var instances = new HashSet<string>(StringComparer.Ordinal);
        var ports = new HashSet<int>();
        foreach (var instance in scenario.Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Id)) result.Errors.Add("every instance requires an id.");
            else if (!instances.Add(instance.Id)) result.Errors.Add($"duplicate instance id '{instance.Id}'.");
            if (instance.Port is < 1 or > 65535) result.Errors.Add($"instance '{instance.Id}' has an invalid port.");
            var effectivePort = instance.Port ?? PlayerProcess.DefaultPort;
            if (!ports.Add(effectivePort)) result.Errors.Add($"port {effectivePort} is used by more than one instance.");
            if (string.IsNullOrWhiteSpace(instance.Executable) && instance.Port is null)
                result.Errors.Add($"instance '{instance.Id}' needs executable or port for an attached Player.");
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        ValidateSteps("preconditions", scenario.Preconditions, instances, stepIds, result);
        ValidateSteps("setup", scenario.Setup, instances, stepIds, result);
        ValidateSteps("actions", scenario.Actions, instances, stepIds, result);
        ValidateSteps("assertions", scenario.Assertions, instances, stepIds, result);
        ValidateSteps("cleanup", scenario.Cleanup, instances, stepIds, result);
        ValidateReferences(scenario, stepIds, result);
    }

    private static void ValidateSteps(string phase, IEnumerable<ScenarioStep> steps, HashSet<string> instances,
        HashSet<string> stepIds, ScenarioValidationResult result)
    {
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id)) result.Errors.Add($"{phase} step requires id.");
            else if (!stepIds.Add(step.Id)) result.Errors.Add($"duplicate step id '{step.Id}'.");
            if (!Kinds.Contains(step.Kind)) result.Errors.Add($"step '{step.Id}' has unsupported kind '{step.Kind}'.");

            var kind = step.Kind.ToLowerInvariant();
            if (kind is not "manual" and not "assert" && (string.IsNullOrWhiteSpace(step.Instance) || !instances.Contains(step.Instance)))
                result.Errors.Add($"step '{step.Id}' references an unknown instance.");
            if (step.TimeoutMs <= 0) result.Errors.Add($"step '{step.Id}' timeoutMs must be positive.");
            if (step.PollMs <= 0) result.Errors.Add($"step '{step.Id}' pollMs must be positive.");
            switch (kind)
            {
                case "command" when string.IsNullOrWhiteSpace(step.Command):
                    result.Errors.Add($"command step '{step.Id}' requires command."); break;
                case "probe" when string.IsNullOrWhiteSpace(step.Probe):
                    result.Errors.Add($"probe step '{step.Id}' requires probe."); break;
                case "wait":
                    if (string.IsNullOrWhiteSpace(step.Probe)) result.Errors.Add($"wait step '{step.Id}' requires probe.");
                    if (step.Condition is null) result.Errors.Add($"wait step '{step.Id}' requires condition.");
                    else ValidateCondition(step.Id, step.Condition, result);
                    break;
                case "assert":
                    if (step.Actual is null) result.Errors.Add($"assert step '{step.Id}' requires actual.");
                    if (string.IsNullOrWhiteSpace(step.Operator)) result.Errors.Add($"assert step '{step.Id}' requires operator.");
                    if (step.Value is null && step.ExpectedFrom is null && step.Operator is not "exists" and not "notExists")
                        result.Errors.Add($"assert step '{step.Id}' requires value or expectedFrom.");
                    if (step.Value is not null && step.ExpectedFrom is not null)
                        result.Errors.Add($"assert step '{step.Id}' cannot specify both value and expectedFrom.");
                    break;
                case "manual" when string.IsNullOrWhiteSpace(step.Reason):
                    result.Errors.Add($"manual step '{step.Id}' requires reason."); break;
            }
        }
    }

    private static void ValidateCondition(string id, ScenarioCondition condition, ScenarioValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(condition.Path)) result.Errors.Add($"wait step '{id}' condition requires path.");
        if (string.IsNullOrWhiteSpace(condition.Operator)) result.Errors.Add($"wait step '{id}' condition requires operator.");
        else if (!ScenarioAssertions.SupportedOperators.Contains(condition.Operator)) result.Errors.Add($"wait step '{id}' has unsupported operator '{condition.Operator}'.");
        if (condition.Value is null && condition.ExpectedFrom is null && condition.Operator is not "exists" and not "notExists")
            result.Errors.Add($"wait step '{id}' condition requires value or expectedFrom.");
        if (condition.Value is not null && condition.ExpectedFrom is not null)
            result.Errors.Add($"wait step '{id}' condition cannot specify both value and expectedFrom.");
    }

    private static void ValidateReferences(RuntimeScenario scenario, HashSet<string> stepIds, ScenarioValidationResult result)
    {
        var orderedSteps = AllSteps(scenario).ToList();
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < orderedSteps.Count; index++)
            if (!order.ContainsKey(orderedSteps[index].Id)) order[orderedSteps[index].Id] = index;

        foreach (var step in orderedSteps)
        {
            if (step.Actual is not null) ValidateReference(step.Id, step.Actual, stepIds, order, result);
            if (step.ExpectedFrom is not null) ValidateReference(step.Id, step.ExpectedFrom, stepIds, order, result);
            if (step.Condition?.ExpectedFrom is not null) ValidateReference(step.Id, step.Condition.ExpectedFrom, stepIds, order, result);
            if (!string.IsNullOrWhiteSpace(step.Operator) && !ScenarioAssertions.SupportedOperators.Contains(step.Operator))
                result.Errors.Add($"assert step '{step.Id}' has unsupported operator '{step.Operator}'.");
        }
    }

    private static void ValidateReference(string stepId, ScenarioReference reference, HashSet<string> stepIds,
        IReadOnlyDictionary<string, int> order, ScenarioValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(reference.Step) || !stepIds.Contains(reference.Step))
        {
            result.Errors.Add($"step '{stepId}' references unknown step '{reference.Step}'.");
            return;
        }
        if (order.TryGetValue(stepId, out var current) && order.TryGetValue(reference.Step, out var referenced) && referenced >= current)
            result.Errors.Add($"step '{stepId}' references future step '{reference.Step}'.");
    }

    private static IEnumerable<ScenarioStep> AllSteps(RuntimeScenario scenario) =>
        scenario.Preconditions.Concat(scenario.Setup).Concat(scenario.Actions).Concat(scenario.Assertions).Concat(scenario.Cleanup);
}

public static class ScenarioJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true
    };
}
