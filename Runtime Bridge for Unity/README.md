# Runtime Bridge for Unity

A local MCP server and CLI for controlling a **built Unity Player**. The Unity
runtime is authoritative for Unity state and command execution; the desktop host
only launches, identifies, queries, and sends commands to that runtime.

The Unity Player-side component is delivered separately as the UPM package
`com.2023k-work.runtime-bridge`. This project contains the MCP/CLI host.

## Host modes

With no arguments (or with `mcp`) the executable runs an MCP stdio server. Any
other argument selects CLI mode:

```powershell
dotnet run --project ".\Runtime Bridge for Unity" -- --help
dotnet run --project ".\Runtime Bridge for Unity" -- app start --exe C:\build\Game.exe --port 4765
dotnet run --project ".\Runtime Bridge for Unity" -- app wait-ready --session <id> --timeout-ms 10000
dotnet run --project ".\Runtime Bridge for Unity" -- app status --session <id>
dotnet run --project ".\Runtime Bridge for Unity" -- command echo --session <id> --payload '{"message":"hello"}'
dotnet run --project ".\Runtime Bridge for Unity" -- app stop --session <id>
```

`app start` persists process metadata in the local session store. Use the
returned session id with `app wait-ready`, `app status`, and `app stop`.
Override the default session directory with `RUNTIME_BRIDGE_STATE_DIR`.

MCP tools expose the same application service: `unity_player_start`,
`unity_player_wait_ready`, `unity_player_status`, `unity_player_command`, and
`unity_player_stop`.

## Unity setup

Install the Unity 6 UPM package from the repository root README URL. The package declares
`com.unity.nuget.newtonsoft-json` and provides `RuntimeBridgeUnity`; attach that
component to a GameObject in the Player's startup scene. The host launches the
Player with:

```text
Game.exe --runtime-bridge-session <id> --runtime-bridge-port 4765 -logFile <path>
```

The bridge binds only to loopback. Except for constant `ping` data, requests are
queued and completed from `Update()`, keeping Unity APIs and registered command
handlers on Unity's main thread.

## Protocol and failure semantics

- One UTF-8 JSON Lines request/response per TCP connection; maximum 1 MiB.
- Every response preserves its request correlation ID.
- A timeout means **outcome unknown**. It does not prove failure or rollback.
- Graceful stop verifies PID, executable path, and process start time; it never
  kills a mismatched process.
- Remote access requires a separate authentication, TLS, authorization, replay,
  and audit design.

## Build and test

```powershell
dotnet build ".\Runtime Bridge for Unity.slnx"
dotnet run --project ".\tests\RuntimeBridge.Tests"
dotnet pack ".\Runtime Bridge for Unity\Runtime Bridge for Unity.csproj" -c Release
```

The tests use a real fake-Player process. They do not validate a real Unity build
or Unity's graphics/main-thread runtime.
