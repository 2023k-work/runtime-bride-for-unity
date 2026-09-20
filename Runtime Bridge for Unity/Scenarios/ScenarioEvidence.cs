using System.Text.Json;

namespace RuntimeBridge.Unity.Scenarios;

internal sealed class ScenarioEvidenceWriter : IAsyncDisposable
{
    private readonly StreamWriter _host;
    private readonly StreamWriter _commands;
    private readonly StreamWriter _probes;

    private ScenarioEvidenceWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        _host = Open(Path.Combine(directory, "host.log"));
        _commands = Open(Path.Combine(directory, "commands.jsonl"));
        _probes = Open(Path.Combine(directory, "probes.jsonl"));
    }

    public string DirectoryPath { get; private init; } = string.Empty;

    public static ScenarioEvidenceWriter Create(string directory) => new(directory) { DirectoryPath = directory };

    public Task HostAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(_host, new { utc = DateTimeOffset.UtcNow, message }, cancellationToken);

    public Task CommandAsync(object evidence, CancellationToken cancellationToken = default) =>
        WriteAsync(_commands, evidence, cancellationToken);

    public Task ProbeAsync(object evidence, CancellationToken cancellationToken = default) =>
        WriteAsync(_probes, evidence, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        await _commands.DisposeAsync().ConfigureAwait(false);
        await _probes.DisposeAsync().ConfigureAwait(false);
    }

    private static StreamWriter Open(string path) => new(path, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

    private static async Task WriteAsync(StreamWriter writer, object value, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value, ScenarioJson.Options).AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
