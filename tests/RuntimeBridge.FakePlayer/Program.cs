using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RuntimeBridge.Unity.Protocol;

var values = Parse(args);
values.TryGetValue("--runtime-bridge-session", out var session);
session ??= "";
var port = values.TryGetValue("--runtime-bridge-port", out var text) && int.TryParse(text, out var parsed) ? parsed : 4765;
if (values.TryGetValue("-logFile", out var log)) await File.AppendAllTextAsync(log, $"fake-player-started session={session}{Environment.NewLine}");
using var stop = new CancellationTokenSource();
var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
try
{
    while (!stop.IsCancellationRequested)
    {
        using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
        await Handle(tcp, session, stop);
    }
}
catch (OperationCanceledException) { }
return 0;

static async Task Handle(TcpClient tcp, string session, CancellationTokenSource stop)
{
    await using var stream = tcp.GetStream();
    using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    var request = JsonSerializer.Deserialize<BridgeRequest>(await reader.ReadLineAsync() ?? "", BridgeJson.Options);
    if (request is null) return;
    BridgeResponse response;
    if (request.Operation != "ping" && session.Length > 0 && request.Session != session)
        response = Fail(request, session, "SESSION_MISMATCH");
    else
    {
        var shutdown = request.Operation == "shutdown";
        JsonElement data = request.Operation switch
        {
            "ping" => JsonSerializer.SerializeToElement(new { service = "fake-player" }),
            "hello" => JsonSerializer.SerializeToElement(new { service = "fake-player", session }),
            "ready" => JsonSerializer.SerializeToElement(new { ready = true, session }),
            "status" => JsonSerializer.SerializeToElement(new { state = "running", session }),
            "command" when request.Command == "echo" => request.Payload ?? JsonSerializer.SerializeToElement(new { }),
            "shutdown" => JsonSerializer.SerializeToElement(new { state = "shutting_down", session }),
            _ => default
        };
        response = data.ValueKind == JsonValueKind.Undefined ? Fail(request, session, "UNKNOWN_COMMAND")
            : new BridgeResponse { Id = request.Id, Operation = request.Operation, Session = session, Ok = true, Data = data };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, BridgeJson.Options));
        if (shutdown) stop.Cancel();
        return;
    }
    await writer.WriteLineAsync(JsonSerializer.Serialize(response, BridgeJson.Options));
}

static BridgeResponse Fail(BridgeRequest request, string session, string code) => new()
{ Id = request.Id, Operation = request.Operation, Session = session, Error = new BridgeError { Code = code, Message = code } };

static Dictionary<string, string> Parse(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i++) if (args[i].StartsWith("--") || args[i] == "-logFile") result[args[i]] = args[++i];
    return result;
}
