using System.Diagnostics;
using System.Net.Sockets;
using RuntimeBridge.Unity.Protocol;

namespace RuntimeBridge.Unity;

public sealed class PlayerProcess(RuntimeSessionStore store)
{
    public const int DefaultPort = 4765;

    public RuntimeSession Start(string executable, string? session, int port, string? logDirectory)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var executablePath = Path.GetFullPath(executable);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Unity Player executable was not found.", executablePath);
        var sessionId = string.IsNullOrWhiteSpace(session) ? Guid.NewGuid().ToString("N") : session;
        _ = store.PathFor(sessionId);
        var directory = logDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuntimeBridgeForUnity", "logs")
            : Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, $"runtime-bridge-{sessionId}.log");
        File.WriteAllText(logPath, $"runtime bridge launch session={sessionId} utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
        var info = new ProcessStartInfo
        {
            FileName = executablePath, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--runtime-bridge-session"); info.ArgumentList.Add(sessionId);
        info.ArgumentList.Add("--runtime-bridge-port"); info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-logFile"); info.ArgumentList.Add(logPath);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Unity Player.");
        try
        {
            var state = RuntimeSession.FromProcess(process, sessionId, executablePath, port, logPath, store.PathFor(sessionId));
            store.Save(state);
            return state;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally { process.Dispose(); }
    }

    public async Task<BridgeResponse> WaitReadyAsync(RuntimeSession session, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var identity = ProcessIdentity.Inspect(session);
            if (!identity.IsRunning) throw new InvalidOperationException("Unity Player exited before ready.");
            if (!identity.Matches) throw new ProcessIdentityException("Tracked PID no longer matches the recorded Unity Player.");
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                return await new BridgeClient(session.Host, session.Port, Min(remaining, TimeSpan.FromMilliseconds(250)))
                    .SendAsync("ready", session: session.Session, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or BridgeProtocolException) { lastError = ex; }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"Unity Player did not become ready within {timeout.TotalMilliseconds:0} ms. Last error: {lastError?.Message ?? "none"}");
    }

    public async Task<(BridgeResponse? Response, string State)> StopAsync(RuntimeSession session, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var identity = ProcessIdentity.Inspect(session);
        if (!identity.IsRunning) return SaveStopped(session, identity.ExitCode, null, "already_stopped");
        if (!identity.Matches) throw new ProcessIdentityException("Tracked PID does not match the recorded Unity Player; refusing to stop it.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        BridgeResponse? response = null;
        Exception? lastError = null;
        while (response is null && DateTimeOffset.UtcNow < deadline)
        {
            var current = ProcessIdentity.Inspect(session);
            if (!current.IsRunning) return SaveStopped(session, current.ExitCode, null, "already_stopped");
            if (!current.Matches) throw new ProcessIdentityException("Tracked PID no longer matches the recorded Unity Player; refusing to stop it.");
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                response = await new BridgeClient(session.Host, session.Port, Min(remaining, TimeSpan.FromMilliseconds(250)))
                    .SendAsync("shutdown", session: session.Session, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException) { lastError = ex; }
            if (response is null) await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        if (response is null) throw new TimeoutException($"Unity Player did not accept shutdown; process was not killed. Last error: {lastError?.Message ?? "none"}");
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = ProcessIdentity.Inspect(session);
            if (!current.IsRunning) return SaveStopped(session, current.ExitCode, response, "stopped");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Unity Player acknowledged shutdown but did not exit; process was not killed.");
    }

    private (BridgeResponse? Response, string State) SaveStopped(RuntimeSession session, int? exitCode, BridgeResponse? response, string state)
    {
        session.Lifecycle = "stopped"; session.ExitCode = exitCode; session.EndedUtc = DateTimeOffset.UtcNow; store.Save(session);
        return (response, state);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
