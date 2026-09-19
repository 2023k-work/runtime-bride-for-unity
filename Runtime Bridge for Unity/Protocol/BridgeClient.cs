using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RuntimeBridge.Unity.Protocol;

public sealed class BridgeClient
{
    public const int MaxFrameBytes = 1024 * 1024;
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    public BridgeClient(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
        if (!System.Net.IPAddress.TryParse(host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("Only numeric loopback endpoints are supported.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _host = host;
        _port = port;
        _timeout = timeout;
    }

    public async Task<BridgeResponse> SendAsync(string operation, string? command = null,
        JsonElement? payload = null, string? session = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("D");
        var request = new BridgeRequest { Id = id, Operation = operation, Command = command, Payload = payload, Session = session };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            var line = JsonSerializer.Serialize(request, BridgeJson.Options);
            if (Encoding.UTF8.GetByteCount(line) > MaxFrameBytes) throw new BridgeProtocolException("Request exceeds the 1 MiB frame limit.");
            await writer.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
            var responseLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (responseLine is null) throw new BridgeProtocolException("Unity bridge closed the connection without a response.");
            if (Encoding.UTF8.GetByteCount(responseLine) > MaxFrameBytes) throw new BridgeProtocolException("Response exceeds the 1 MiB frame limit.");
            BridgeResponse? response;
            try { response = JsonSerializer.Deserialize<BridgeResponse>(responseLine, BridgeJson.Options); }
            catch (JsonException ex) { throw new BridgeProtocolException($"Invalid response JSON: {ex.Message}"); }
            if (response is null || response.Type != "response") throw new BridgeProtocolException("Response has an invalid type.");
            if (!string.Equals(response.Id, id, StringComparison.Ordinal)) throw new BridgeProtocolException("Response correlation id mismatch.");
            if (!response.Ok) throw new BridgeRemoteException(response.Error?.Code ?? "REMOTE_ERROR", response.Error?.Message ?? "Unknown bridge error.");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response from Unity bridge within {_timeout.TotalMilliseconds:0} ms.");
        }
    }
}
