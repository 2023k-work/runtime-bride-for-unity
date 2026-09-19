using System.Net.Sockets;
using System.Text.Json;
using RuntimeBridge.Unity.Protocol;

namespace RuntimeBridge.Unity.Cli;

internal static class CliApplication
{
    private const string Usage = """
        Runtime Bridge for Unity

        MCP mode (default): runtime-bridge-unity [mcp]
        CLI:
          runtime-bridge-unity app start --exe <Player.exe> [--port <n>] [--session <id>]
          runtime-bridge-unity app status|wait-ready|stop --session <id> [--timeout-ms <n>]
          runtime-bridge-unity ping [--port <n>]
          runtime-bridge-unity connect --session <id> [--port <n>]
          runtime-bridge-unity command <name> [--session <id>] [--payload <json>] [--port <n>]
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (options.Command is "help") return Emit(new { ok = true, help = Usage });
            if (options.Command is "version") return Emit(new { ok = true, version = "runtime-bridge-unity 0.1.0-beta" });
            var store = new RuntimeSessionStore();
            var service = new RuntimeBridgeService(store, new PlayerProcess(store));
            object result = options.Command switch
            {
                "ping" => await service.PingAsync(options.Port, options.Timeout),
                "connect" => await service.ConnectAsync(Required(options.Session, "--session"), options.Port, options.Timeout),
                "command" => await service.SendCommandAsync(Required(options.Name, "command name"), options.Payload, options.Session, options.Port, options.Timeout),
                "app" => await RunAppAsync(service, options),
                _ => throw new CliUsageException($"Unknown command '{options.Command}'.")
            };
            return Emit(result, options.Pretty);
        }
        catch (CliUsageException ex) { return Error("USAGE", ex.Message, 2); }
        catch (SessionException ex) { return Error("SESSION_NOT_FOUND", ex.Message, 11); }
        catch (ProcessIdentityException ex) { return Error("PROCESS_IDENTITY", ex.Message, 12); }
        catch (BridgeRemoteException ex) { return Error(ex.Code, ex.Message, ex.Code == "SESSION_MISMATCH" ? 11 : 3); }
        catch (TimeoutException ex) { return Error("TIMEOUT_UNKNOWN", ex.Message, 4); }
        catch (SocketException ex) { return Error("CONNECTION", ex.Message, 5); }
        catch (BridgeProtocolException ex) { return Error("PROTOCOL", ex.Message, 6); }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException) { return Error("ERROR", ex.Message, 1); }
    }

    private static async Task<object> RunAppAsync(RuntimeBridgeService service, Options options) => options.Action switch
    {
        "start" => new { ok = true, process = service.StartPlayer(Required(options.Executable, "--exe"), options.Port, options.Session).ToPublicMetadata() },
        "status" => await service.GetPlayerStatusAsync(Required(options.Session, "--session"), options.Timeout),
        "wait-ready" => await service.WaitReadyAsync(Required(options.Session, "--session"), options.Timeout),
        "stop" => await service.StopPlayerAsync(Required(options.Session, "--session"), options.Timeout),
        _ => throw new CliUsageException("app requires start, status, wait-ready, or stop.")
    };

    private static Options Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new CliUsageException("A command is required.");
        var values = new List<string>(); var result = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h": result.Command = "help"; break;
                case "--version": result.Command = "version"; break;
                case "--pretty": result.Pretty = true; break;
                case "--port": result.Port = PositiveInt(Read(args, ref i), "--port", 65535); break;
                case "--timeout-ms": result.Timeout = PositiveInt(Read(args, ref i), "--timeout-ms", int.MaxValue); break;
                case "--session": result.Session = Read(args, ref i); break;
                case "--exe": result.Executable = Read(args, ref i); break;
                case "--payload": result.Payload = Read(args, ref i); break;
                default:
                    if (args[i].StartsWith('-')) throw new CliUsageException($"Unknown option '{args[i]}'.");
                    values.Add(args[i]); break;
            }
        }
        if (result.Command.Length == 0) result.Command = values.FirstOrDefault() ?? throw new CliUsageException("A command is required.");
        if (result.Command == "app") result.Action = values.ElementAtOrDefault(1);
        if (result.Command == "command") result.Name = values.ElementAtOrDefault(1);
        return result;
    }

    private static string Read(IReadOnlyList<string> args, ref int i) => ++i < args.Count ? args[i] : throw new CliUsageException("Option requires a value.");
    private static int PositiveInt(string text, string option, int max) => int.TryParse(text, out var value) && value is > 0 && value <= max ? value : throw new CliUsageException($"{option} has an invalid value.");
    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new CliUsageException($"{name} is required.");
    private static int Emit(object value, bool pretty = false, int code = 0) { Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(BridgeJson.Options) { WriteIndented = pretty })); return code; }
    private static int Error(string code, string message, int exitCode) => Emit(new { ok = false, error = new { code, message } }, code: exitCode);

    private sealed class Options
    {
        public string Command { get; set; } = string.Empty; public string? Action { get; set; }
        public string? Name { get; set; } public string? Session { get; set; } public string? Executable { get; set; }
        public string? Payload { get; set; } public int Port { get; set; } = PlayerProcess.DefaultPort;
        public int Timeout { get; set; } = 3000; public bool Pretty { get; set; }
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
