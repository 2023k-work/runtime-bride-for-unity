using System.Globalization;
using System.Text.Json.Nodes;

namespace RuntimeBridge.Unity.Scenarios;

public sealed record ScenarioAssertionEvaluation(bool Passed, string Message, JsonNode? Actual, JsonNode? Expected);

public static class ScenarioAssertions
{
    public static readonly IReadOnlySet<string> SupportedOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual",
        "exists", "notExists", "contains", "changed", "changedFrom", "unchanged", "unchangedFrom"
    };

    public static ScenarioAssertionEvaluation Evaluate(JsonNode? actual, string operation, JsonNode? expected)
    {
        var passed = operation switch
        {
            "equals" => JsonNode.DeepEquals(actual, expected),
            "notEquals" => !JsonNode.DeepEquals(actual, expected),
            "greaterThan" => CompareNumbers(actual, expected, (left, right) => left > right),
            "greaterThanOrEqual" => CompareNumbers(actual, expected, (left, right) => left >= right),
            "lessThan" => CompareNumbers(actual, expected, (left, right) => left < right),
            "lessThanOrEqual" => CompareNumbers(actual, expected, (left, right) => left <= right),
            "exists" => actual is not null,
            "notExists" => actual is null,
            "contains" => Contains(actual, expected),
            "changed" or "changedFrom" => !JsonNode.DeepEquals(actual, expected),
            "unchanged" or "unchangedFrom" => JsonNode.DeepEquals(actual, expected),
            _ => throw new ScenarioDefinitionException($"Unsupported assertion operator '{operation}'.")
        };
        var message = passed ? "Assertion passed." : $"Assertion '{operation}' failed.";
        return new(passed, message, actual, expected);
    }

    private static bool Contains(JsonNode? actual, JsonNode? expected)
    {
        if (actual is JsonArray array) return array.Any(item => JsonNode.DeepEquals(item, expected));
        if (actual is JsonValue value && value.TryGetValue<string>(out var text)
            && expected is JsonValue expectedValue && expectedValue.TryGetValue<string>(out var fragment))
            return text.Contains(fragment, StringComparison.Ordinal);
        return false;
    }

    private static bool CompareNumbers(JsonNode? actual, JsonNode? expected, Func<decimal, decimal, bool> compare)
    {
        return TryDecimal(actual, out var left) && TryDecimal(expected, out var right) && compare(left, right);
    }

    private static bool TryDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is null) return false;
        return decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static JsonNode? Resolve(IReadOnlyDictionary<string, JsonNode?> values, ScenarioReference reference)
    {
        if (!values.TryGetValue(reference.Step, out var value))
            throw new ScenarioDefinitionException($"Reference points to unknown or future step '{reference.Step}'.");
        return ResolvePath(value, reference.Path);
    }

    public static JsonNode? ResolvePath(JsonNode? root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root?.DeepClone();
        var current = root;
        foreach (var rawPart in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart;
            while (part.Length > 0)
            {
                var bracket = part.IndexOf('[');
                var property = bracket >= 0 ? part[..bracket] : part;
                if (property.Length > 0)
                {
                    if (current is not JsonObject obj || !obj.TryGetPropertyValue(property, out current)) return null;
                }
                if (bracket < 0) break;
                var end = part.IndexOf(']', bracket + 1);
                if (end < 0 || !int.TryParse(part[(bracket + 1)..end], out var index)) return null;
                if (current is not JsonArray array || index < 0 || index >= array.Count) return null;
                current = array[index];
                part = end + 1 < part.Length ? part[(end + 1)..] : string.Empty;
            }
        }
        return current?.DeepClone();
    }
}
