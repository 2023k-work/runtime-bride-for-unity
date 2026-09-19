using System.Diagnostics;
using System.Text.Json;
using RuntimeBridge.Unity.Protocol;

namespace RuntimeBridge.Unity;

public sealed class RuntimeSession
{
    public string Session { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public int Port { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public string LogPath { get; init; } = string.Empty;
    public string StatePath { get; set; } = string.Empty;
    public string Lifecycle { get; set; } = "running";
    public int? ExitCode { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }

    public object ToPublicMetadata(string? state = null, bool? processIdentity = null) => new
    {
        session = Session, pid = ProcessId, executable = ExecutablePath, startedUtc = StartedUtc,
        host = Host, port = Port, logPath = LogPath, statePath = StatePath,
        state = state ?? Lifecycle, exitCode = ExitCode, endedUtc = EndedUtc, processIdentity
    };

    public static RuntimeSession FromProcess(Process process, string session, string executablePath,
        int port, string logPath, string statePath) => new()
    {
        Session = session, ProcessId = process.Id, ExecutablePath = executablePath,
        StartedUtc = process.StartTime.ToUniversalTime(), Port = port, LogPath = logPath,
        StatePath = statePath
    };
}

public sealed class RuntimeSessionStore
{
    private readonly string _directory;

    public RuntimeSessionStore() : this(null) { }

    public RuntimeSessionStore(string? directory)
    {
        _directory = directory ?? Environment.GetEnvironmentVariable("RUNTIME_BRIDGE_STATE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuntimeBridgeForUnity", "sessions");
        Directory.CreateDirectory(_directory);
    }

    public string PathFor(string session)
    {
        if (string.IsNullOrWhiteSpace(session) || session.Length > 100 || session.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Session must contain only letters, digits, '-', '_', or '.'.", nameof(session));
        return Path.Combine(_directory, session + ".json");
    }

    public RuntimeSession? Load(string session)
    {
        var path = PathFor(session);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RuntimeSession>(File.ReadAllText(path), BridgeJson.Options)
            ?? throw new InvalidDataException($"Session state file is invalid: {path}");
    }

    public void Save(RuntimeSession session)
    {
        var path = PathFor(session.Session);
        session.StatePath = path;
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(session, BridgeJson.Options));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record ProcessIdentityResult(bool IsRunning, bool Matches, int? ExitCode = null);

public static class ProcessIdentity
{
    public static ProcessIdentityResult Inspect(RuntimeSession session)
    {
        Process process;
        try { process = Process.GetProcessById(session.ProcessId); }
        catch (ArgumentException) { return new(false, false); }
        using (process)
        {
            try
            {
                var exited = process.HasExited;
                var actualPath = exited ? null : Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                var matches = !exited && string.Equals(actualPath, Path.GetFullPath(session.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                    && process.StartTime.ToUniversalTime() == session.StartedUtc;
                return new(!exited, matches, exited ? process.ExitCode : null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                return new(!process.HasExited, false);
            }
        }
    }
}

public sealed class ProcessIdentityException(string message) : Exception(message);
public sealed class SessionException(string message) : Exception(message);
