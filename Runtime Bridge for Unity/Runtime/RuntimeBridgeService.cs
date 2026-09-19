using System.Net.Sockets;
using System.Text.Json;
using RuntimeBridge.Unity.Protocol;

namespace RuntimeBridge.Unity;

public sealed class RuntimeBridgeService(RuntimeSessionStore sessions, PlayerProcess players)
{
    public RuntimeSession StartPlayer(string executable, int port = PlayerProcess.DefaultPort,
        string? session = null, string? logDirectory = null) => players.Start(executable, session, port, logDirectory);

    public async Task<object> WaitReadyAsync(string session, int timeoutMilliseconds = 10_000, CancellationToken cancellationToken = default)
    {
        var state = RequireSession(session);
        var response = await players.WaitReadyAsync(state, Timeout(timeoutMilliseconds), cancellationToken).ConfigureAwait(false);
        return new { ok = true, ready = true, process = state.ToPublicMetadata(), response };
    }

    public async Task<object> StopPlayerAsync(string session, int timeoutMilliseconds = 10_000, CancellationToken cancellationToken = default)
    {
        var state = RequireSession(session);
        var stopped = await players.StopAsync(state, Timeout(timeoutMilliseconds), cancellationToken).ConfigureAwait(false);
        return new { ok = true, state = stopped.State, response = stopped.Response, process = state.ToPublicMetadata("stopped") };
    }

    public async Task<object> GetPlayerStatusAsync(string session, int timeoutMilliseconds = 3_000, CancellationToken cancellationToken = default)
    {
        var state = RequireSession(session);
        var identity = ProcessIdentity.Inspect(state);
        var stateName = !identity.IsRunning ? "stopped" : identity.Matches ? "running" : "identity_mismatch";
        if (!identity.IsRunning && state.Lifecycle != "stopped")
        {
            state.Lifecycle = "stopped"; state.ExitCode = identity.ExitCode; state.EndedUtc = DateTimeOffset.UtcNow; sessions.Save(state);
        }
        object? bridge = null;
        if (identity.IsRunning && identity.Matches)
        {
            try
            {
                var response = await Client(state.Host, state.Port, timeoutMilliseconds).SendAsync("status", session: state.Session, cancellationToken: cancellationToken).ConfigureAwait(false);
                bridge = new { ok = true, response };
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or BridgeProtocolException or BridgeRemoteException)
            {
                bridge = new { ok = false, error = ex.Message };
            }
        }
        return new { ok = stateName != "identity_mismatch", state = stateName, process = state.ToPublicMetadata(stateName, identity.Matches), bridge };
    }

    public Task<BridgeResponse> PingAsync(int port = PlayerProcess.DefaultPort, int timeoutMilliseconds = 3_000, CancellationToken cancellationToken = default) =>
        Client("127.0.0.1", port, timeoutMilliseconds).SendAsync("ping", cancellationToken: cancellationToken);

    public Task<BridgeResponse> ConnectAsync(string session, int port = PlayerProcess.DefaultPort, int timeoutMilliseconds = 3_000, CancellationToken cancellationToken = default) =>
        Client("127.0.0.1", port, timeoutMilliseconds).SendAsync("hello", session: session, cancellationToken: cancellationToken);

    public Task<BridgeResponse> SendCommandAsync(string command, string? payloadJson, string? session,
        int port = PlayerProcess.DefaultPort, int timeoutMilliseconds = 3_000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));
        using var document = JsonDocument.Parse(payloadJson ?? "{}");
        return Client("127.0.0.1", port, timeoutMilliseconds)
            .SendAsync("command", command, document.RootElement.Clone(), session, cancellationToken);
    }

    private RuntimeSession RequireSession(string session) => sessions.Load(session) ?? throw new SessionException($"Session '{session}' was not found.");
    private static TimeSpan Timeout(int milliseconds) => milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : throw new ArgumentOutOfRangeException(nameof(milliseconds));
    private static BridgeClient Client(string host, int port, int timeoutMilliseconds) => new(host, port, Timeout(timeoutMilliseconds));
}
