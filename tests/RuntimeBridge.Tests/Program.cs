using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RuntimeBridge.Unity;
using RuntimeBridge.Unity.Protocol;

var tests = new (string Name, Func<Task> Run)[]
{
    ("protocol preserves correlation and payload", ProtocolRoundTrip),
    ("protocol timeout is unknown", TimeoutIsSurfaced),
    ("non-loopback endpoint is rejected", RemoteEndpointRejected),
    ("player lifecycle verifies session and shuts down", PlayerLifecycle)
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
