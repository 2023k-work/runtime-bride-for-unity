using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuntimeBridge.Unity;
using RuntimeBridge.Unity.Protocol;
using RuntimeBridge.Unity.Scenarios;

var tests = new (string Name, Func<Task> Run)[]
{
    ("protocol preserves correlation and payload", ProtocolRoundTrip),
    ("protocol timeout is unknown", TimeoutIsSurfaced),
    ("non-loopback endpoint is rejected", RemoteEndpointRejected),
    ("player lifecycle verifies session and shuts down", PlayerLifecycle),
    ("scenario validator rejects unsupported documents", ScenarioValidatorRejects),
    ("scenario assertions compare values and snapshots", ScenarioAssertionsWork),
    ("scenario runner executes phases and writes evidence", ScenarioRunnerLifecycle),
    ("scenario runner blocks unavailable attached players", ScenarioRunnerBlocksAttachedPlayer)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static async Task ProtocolRoundTrip()
{
    await using var server = new FakeServer(request => Task.FromResult<BridgeResponse?>(new BridgeResponse { Id = request.Id, Operation = request.Operation, Ok = true, Data = request.Payload }));
    using var document = JsonDocument.Parse("{\"value\":42}");
    var response = await new BridgeClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(2)).SendAsync("command", "echo", document.RootElement.Clone());
    Assert(response.Data?.GetProperty("value").GetInt32() == 42, "Payload was not preserved.");
}

static async Task TimeoutIsSurfaced()
{
    await using var server = new FakeServer(async _ => { await Task.Delay(500); return null; });
    try { await new BridgeClient("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(30)).SendAsync("ping"); throw new Exception("Expected timeout."); }
    catch (TimeoutException) { }
}

static Task RemoteEndpointRejected()
{
    try { _ = new BridgeClient("192.0.2.1", 4765, TimeSpan.FromSeconds(1)); throw new Exception("Expected loopback validation."); }
    catch (ArgumentException) { return Task.CompletedTask; }
}

static async Task PlayerLifecycle()
{
    var stateDirectory = Path.Combine(Path.GetTempPath(), "runtime-bridge-tests-" + Guid.NewGuid().ToString("N"));
    var store = new RuntimeSessionStore(stateDirectory);
    var service = new RuntimeBridgeService(store, new PlayerProcess(store));
    var session = service.StartPlayer(FakePlayerPath(), FreePort());
    try
    {
        await service.WaitReadyAsync(session.Session, 3000);
        var response = await service.SendCommandAsync("echo", "{\"ok\":true}", session.Session, session.Port, 1000);
        Assert(response.Data?.GetProperty("ok").GetBoolean() == true, "Command did not reach fake Player.");
        try { await service.SendCommandAsync("echo", "{}", "wrong", session.Port, 1000); throw new Exception("Expected session mismatch."); }
        catch (BridgeRemoteException ex) when (ex.Code == "SESSION_MISMATCH") { }
        await service.StopPlayerAsync(session.Session, 3000);
        Assert(!ProcessIdentity.Inspect(session).IsRunning, "Player is still running.");
    }
    finally
    {
        if (ProcessIdentity.Inspect(session).IsRunning) System.Diagnostics.Process.GetProcessById(session.ProcessId).Kill(true);
        try { Directory.Delete(stateDirectory, true); } catch { }
    }
}

static Task ScenarioValidatorRejects()
{
    var path = Path.Combine(Path.GetTempPath(), "runtime-scenario-invalid-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        File.WriteAllText(path, "{\"version\":2,\"name\":\"invalid\",\"instances\":[],\"setup\":[],\"actions\":[],\"assertions\":[],\"cleanup\":[],\"unexpected\":true}");
        var stateDirectory = Path.Combine(Path.GetTempPath(), "runtime-scenario-validation-" + Guid.NewGuid().ToString("N"));
        var store = new RuntimeSessionStore(stateDirectory);
        var result = new RuntimeScenarioRunner(new RuntimeBridgeService(store, new PlayerProcess(store))).Validate(path);
        Assert(!result.Valid && result.Errors.Count > 0, "Invalid scenario was accepted.");
        try { Directory.Delete(stateDirectory, true); } catch { }
        return Task.CompletedTask;
    }
    finally { try { File.Delete(path); } catch { } }
}

static Task ScenarioAssertionsWork()
{
    var before = JsonNode.Parse("{\"phase\":\"Charging\",\"count\":2}")!;
    var after = JsonNode.Parse("{\"phase\":\"Prepared\",\"count\":4}")!;
    Assert(ScenarioAssertions.Evaluate(after["count"], "greaterThan", JsonValue.Create(3)).Passed, "Numeric assertion failed.");
    Assert(ScenarioAssertions.Evaluate(after["phase"], "changedFrom", before["phase"]).Passed, "Snapshot assertion failed.");
    Assert(!ScenarioAssertions.Evaluate(after["phase"], "equals", before["phase"]).Passed, "Negative assertion failed.");
    return Task.CompletedTask;
}

static async Task ScenarioRunnerLifecycle()
{
    var stateDirectory = Path.Combine(Path.GetTempPath(), "runtime-scenario-state-" + Guid.NewGuid().ToString("N"));
    var outputDirectory = Path.Combine(Path.GetTempPath(), "runtime-scenario-results-" + Guid.NewGuid().ToString("N"));
    var scenarioPath = Path.Combine(Path.GetTempPath(), "runtime-scenario-" + Guid.NewGuid().ToString("N") + ".json");
    var port = FreePort();
    var executable = JsonSerializer.Serialize(FakePlayerPath());
    File.WriteAllText(scenarioPath, "{\n" +
        "  \"version\": 1,\n" +
        "  \"name\": \"fake-runner-lifecycle\",\n" +
        "  \"instances\": [{\"id\":\"player\",\"executable\":" + executable + ",\"port\":" + port + "}],\n" +
        "  \"setup\": [{\"id\":\"before\",\"kind\":\"probe\",\"instance\":\"player\",\"probe\":\"Scenario\"}],\n" +
        "  \"actions\": [{\"id\":\"advance\",\"kind\":\"command\",\"instance\":\"player\",\"command\":\"scenario.advance\"}],\n" +
        "  \"assertions\": [" +
        "{\"id\":\"wait-prepared\",\"kind\":\"wait\",\"instance\":\"player\",\"probe\":\"Scenario\",\"condition\":{\"path\":\"data.phase\",\"operator\":\"equals\",\"value\":\"Prepared\"},\"timeoutMs\":1000,\"pollMs\":10}," +
        "{\"id\":\"changed\",\"kind\":\"assert\",\"actual\":{\"step\":\"wait-prepared\",\"path\":\"value.data.phase\"},\"operator\":\"changedFrom\",\"expectedFrom\":{\"step\":\"before\",\"path\":\"data.phase\"}}],\n" +
        "  \"cleanup\": []\n" +
        "}");
    try
    {
        var store = new RuntimeSessionStore(stateDirectory);
        var runner = new RuntimeScenarioRunner(new RuntimeBridgeService(store, new PlayerProcess(store)));
        var batch = await runner.RunPathAsync(scenarioPath, new ScenarioRunOptions { OutputDirectory = outputDirectory, GitCommit = "test-commit" });
        Assert(batch.Status == ScenarioStatus.Pass, $"Scenario status was {batch.Status}.");
        var result = batch.Results.Single();
        Assert(result.Steps.All(step => step.Status == ScenarioStatus.Pass), "A scenario step did not pass.");
        var resultPath = Path.Combine(result.EvidenceDirectory, "result.json");
        Assert(File.Exists(resultPath), "result.json was not written.");
        var resultJson = File.ReadAllText(resultPath);
        Assert(resultJson.Contains("\"status\": \"PASS\"", StringComparison.Ordinal), "result.json did not record PASS.");
        Assert(resultJson.Contains("\"gitCommit\": \"test-commit\"", StringComparison.Ordinal), "result.json did not record build metadata.");
        Assert(File.Exists(Path.Combine(result.EvidenceDirectory, "commands.jsonl")), "commands.jsonl was not written.");
        Assert(File.Exists(Path.Combine(result.EvidenceDirectory, "probes.jsonl")), "probes.jsonl was not written.");
        Assert(!Directory.EnumerateFiles(stateDirectory, "*.json").Any(file => File.ReadAllText(file).Contains("\"state\":\"running\"", StringComparison.Ordinal)), "Managed Player was not cleaned up.");
    }
    finally
    {
        try { File.Delete(scenarioPath); } catch { }
        try { Directory.Delete(stateDirectory, true); } catch { }
        try { Directory.Delete(outputDirectory, true); } catch { }
    }
}

static async Task ScenarioRunnerBlocksAttachedPlayer()
{
    var stateDirectory = Path.Combine(Path.GetTempPath(), "runtime-scenario-blocked-state-" + Guid.NewGuid().ToString("N"));
    var outputDirectory = Path.Combine(Path.GetTempPath(), "runtime-scenario-blocked-results-" + Guid.NewGuid().ToString("N"));
    var scenarioPath = Path.Combine(Path.GetTempPath(), "runtime-scenario-blocked-" + Guid.NewGuid().ToString("N") + ".json");
    await using var server = new FakeServer(request => Task.FromResult<BridgeResponse?>(new BridgeResponse
    {
        Id = request.Id, Operation = request.Operation, Ok = false,
        Error = new BridgeError { Code = "SESSION_MISMATCH", Message = "attached test double rejected the session" }
    }));
    File.WriteAllText(scenarioPath, "{\n" +
        "  \"version\":1,\"name\":\"attached-blocked\",\"instances\":[{\"id\":\"client\",\"port\":" + server.Port + ",\"session\":\"missing\"}]," +
        "  \"setup\":[],\"actions\":[],\"assertions\":[],\"cleanup\":[]\n}");
    try
    {
        var store = new RuntimeSessionStore(stateDirectory);
        var runner = new RuntimeScenarioRunner(new RuntimeBridgeService(store, new PlayerProcess(store)));
        var batch = await runner.RunPathAsync(scenarioPath, new ScenarioRunOptions { OutputDirectory = outputDirectory });
        Assert(batch.Status == ScenarioStatus.Blocked, $"Attached failure was classified as {batch.Status}.");
        Assert(batch.Results.Single().Errors.Any(error => error.Contains("does not own its process", StringComparison.Ordinal)), "Blocked reason was not recorded.");
    }
    finally
    {
        try { File.Delete(scenarioPath); } catch { }
        try { Directory.Delete(stateDirectory, true); } catch { }
        try { Directory.Delete(outputDirectory, true); } catch { }
    }
}

static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
static string FakePlayerPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "RuntimeBridge.FakePlayer", "bin", "Debug", "net10.0", "win-x64", "RuntimeBridge.FakePlayer.exe"));
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class FakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<BridgeRequest, Task<BridgeResponse?>> _handler;
    private readonly Task _loop;
    public FakeServer(Func<BridgeRequest, Task<BridgeResponse?>> handler) { _handler = handler; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _loop = Loop(); }
    public int Port { get; }
    private async Task Loop() { try { while (true) _ = Handle(await _listener.AcceptTcpClientAsync(_stop.Token)); } catch (OperationCanceledException) { } }
    private async Task Handle(TcpClient tcp)
    {
        using (tcp) await using (var stream = tcp.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true))
        {
            var request = JsonSerializer.Deserialize<BridgeRequest>(await reader.ReadLineAsync() ?? "", BridgeJson.Options)!;
            var response = await _handler(request); if (response is null) return;
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, BridgeJson.Options));
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); try { await _loop; } catch (ObjectDisposedException) { } _stop.Dispose(); }
}
