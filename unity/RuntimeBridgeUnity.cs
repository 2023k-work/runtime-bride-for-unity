// Requires UPM package com.unity.nuget.newtonsoft-json.
// Add this component to a GameObject in the Player's startup scene.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class RuntimeBridgeUnity : MonoBehaviour
{
    [Serializable]
    private sealed class Request
    {
        public string type;
        public string id;
        public string operation;
        public string session;
        public string command;
        public JToken payload;
    }

    private sealed class Response
    {
        [JsonProperty("type")] public string Type = "response";
        [JsonProperty("id")] public string Id;
        [JsonProperty("ok")] public bool Ok;
        [JsonProperty("operation")] public string Operation;
        [JsonProperty("session", NullValueHandling = NullValueHandling.Ignore)] public string Session;
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public JToken Data;
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public Error Error;
    }

    private sealed class Error { public string code; public string message; }
    private sealed class Pending
    {
        public Request Request;
        public TaskCompletionSource<Response> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentQueue<Pending> _requests = new();
    private readonly ConcurrentQueue<bool> _shutdown = new();
    private readonly Dictionary<string, Func<JToken, JToken>> _handlers = new();
    private CancellationTokenSource _stop;
    private TcpListener _listener;
    private Task _acceptLoop;
    private string _session;
    private bool _enabled;

    public void RegisterCommand(string name, Func<JToken, JToken> handler)
    {
        if (string.IsNullOrWhiteSpace(name) || handler == null) throw new ArgumentException("Command and handler are required.");
        _handlers[name] = handler;
    }

    private void Start()
    {
        _session = ReadArgument("--runtime-bridge-session");
        var portText = ReadArgument("--runtime-bridge-port");
        var port = string.IsNullOrEmpty(portText) ? 4765 : int.TryParse(portText, out var parsed) ? parsed : -1;
        if (port < 1 || port > 65535) { Debug.LogError("RuntimeBridgeUnity: invalid --runtime-bridge-port."); return; }
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        try { _listener.Start(); }
        catch (SocketException ex) { _stop.Dispose(); _stop = null; Debug.LogError("RuntimeBridgeUnity: cannot listen: " + ex.Message); return; }
        _enabled = true;
        _acceptLoop = AcceptLoopAsync(_stop.Token);
        RegisterCommand("echo", payload => payload ?? new JObject());
        RegisterCommand("smoke", _ => new JObject { ["passed"] = true, ["frame"] = Time.frameCount });
    }

    private void Update()
    {
        while (_requests.TryDequeue(out var pending))
        {
            var request = pending.Request;
            Response response;
            switch (request.operation)
            {
                case "hello": response = Success(request, Identity(false)); break;
                case "ready": response = Success(request, Identity(true)); break;
                case "status": response = Success(request, new JObject { ["service"] = "runtime-bridge-unity", ["session"] = _session, ["state"] = "running", ["unityVersion"] = Application.unityVersion, ["frame"] = Time.frameCount }); break;
                case "command": response = Execute(request); break;
                case "shutdown": response = Success(request, new JObject { ["state"] = "shutting_down" }); break;
                default: response = Failure(request, "UNSUPPORTED_OPERATION", "The operation is not supported."); break;
            }
            pending.Completion.TrySetResult(response);
        }
        while (_shutdown.TryDequeue(out _)) Application.Quit();
    }

    private JObject Identity(bool ready) => new() { ["service"] = "runtime-bridge-unity", ["session"] = _session, ["ready"] = ready, ["unityVersion"] = Application.unityVersion };

    private Response Execute(Request request)
    {
        if (!_handlers.TryGetValue(request.command ?? string.Empty, out var handler)) return Failure(request, "UNKNOWN_COMMAND", "No handler is registered for this command.");
        try { return Success(request, handler(request.payload)); }
        catch (Exception ex) { return Failure(request, "COMMAND_FAILED", ex.Message); }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) _ = HandleClientAsync(await _listener.AcceptTcpClientAsync().ConfigureAwait(false), token); }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" })
        {
            try
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(line) || Encoding.UTF8.GetByteCount(line) > 1024 * 1024) return;
                var request = JsonConvert.DeserializeObject<Request>(line);
                if (request == null || request.type != "request" || string.IsNullOrEmpty(request.id)) { await Write(writer, Failure(request, "BAD_REQUEST", "Invalid request envelope.")); return; }
                if (request.operation != "ping" && !string.IsNullOrEmpty(_session) && !string.Equals(request.session, _session, StringComparison.Ordinal)) { await Write(writer, Failure(request, "SESSION_MISMATCH", "The request session does not match this Player.")); return; }
                Response response;
                if (request.operation == "ping") response = Success(request, new JObject { ["service"] = "runtime-bridge-unity" });
                else
                {
                    var pending = new Pending { Request = request };
                    _requests.Enqueue(pending);
                    response = await Await(pending, token).ConfigureAwait(false);
                }
                await Write(writer, response);
                if (request.operation == "shutdown" && response.Ok) _shutdown.Enqueue(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.LogError("RuntimeBridgeUnity client error: " + ex); }
        }
    }

    private static Task Write(StreamWriter writer, Response response) => writer.WriteLineAsync(JsonConvert.SerializeObject(response));
    private Response Success(Request request, JToken data) => new() { Id = request?.id ?? "", Operation = request?.operation ?? "", Session = _session, Ok = true, Data = data };
    private Response Failure(Request request, string code, string message) => new() { Id = request?.id ?? "", Operation = request?.operation ?? "", Session = _session, Error = new Error { code = code, message = message } };

    private static async Task<Response> Await(Pending pending, CancellationToken token)
    {
        var cancelled = new TaskCompletionSource<bool>();
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(pending.Completion.Task, cancelled.Task).ConfigureAwait(false) == cancelled.Task) token.ThrowIfCancellationRequested();
            return await pending.Completion.Task.ConfigureAwait(false);
        }
    }

    private void OnDestroy()
    {
        if (!_enabled) return;
        _stop.Cancel(); _listener.Stop();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
    }

    private static string ReadArgument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return null;
    }
}
