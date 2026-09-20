# Runtime Bridge for Unity

Runtime Bridge for Unity connects a built Unity 6 Player to local automation
through an MCP server and a JSON CLI. The Player owns Unity state and executes
Unity commands on its main thread; the desktop host owns process lifecycle and
transport orchestration.

## Unity package

Install the Unity 6 UPM package from Unity Package Manager with **Add package
from git URL**:

```text
https://github.com/2023k-work/runtime-bride-for-unity.git?path=Packages/com.2023k-work.runtime-bridge#v0.1.1
```

Package id: `com.2023k-work.runtime-bridge`

Minimum Unity version: Unity 6 (`6000.0`)

The package provides `RuntimeBridgeUnity`, which binds to `127.0.0.1` and
dispatches `hello`, `ready`, `status`, `command`, and `shutdown` through
Unity's `Update()` loop. `ping` is a constant liveness response handled by the
network worker.

After installing, add `RuntimeBridgeUnity` to a GameObject in the Player's
startup scene. The host passes these arguments when it launches the Player:

```text
Game.exe --runtime-bridge-session <session> --runtime-bridge-port 4765 -logFile <path>
```

Package documentation and the sample command handlers are in
[`Packages/com.2023k-work.runtime-bridge`](Packages/com.2023k-work.runtime-bridge).

## MCP and CLI host

Run without arguments (or with `mcp`) to start the MCP stdio server. Run with a
CLI command to use the lifecycle/command client:

```powershell
dotnet run --project ".\Runtime Bridge for Unity" -- mcp
dotnet run --project ".\Runtime Bridge for Unity" -- app start --exe C:\build\Game.exe --port 4765
dotnet run --project ".\Runtime Bridge for Unity" -- app wait-ready --session <session>
dotnet run --project ".\Runtime Bridge for Unity" -- app status --session <session>
dotnet run --project ".\Runtime Bridge for Unity" -- command echo --session <session> --payload '{"message":"hello"}'
dotnet run --project ".\Runtime Bridge for Unity" -- app stop --session <session>
```

The MCP tools and CLI share the same application service:

- `unity_player_start`
- `unity_player_wait_ready`
- `unity_player_status`
- `unity_player_command`
- `unity_player_stop`
- `unity_scenario_validate`
- `unity_scenario_run`
- `unity_scenario_list`

## Runtime Scenarios

Runtime Scenario Runner v1 executes versioned JSON specifications using the
same Player lifecycle and command service as the CLI and MCP adapters. It can
start or attach to named instances, run `setup -> actions -> assertions ->
cleanup`, poll structured Probes, compare snapshots, and write repeatable
evidence under `TestResults/<scenario>/<run-id>/`.

```powershell
dotnet run --project ".\Runtime Bridge for Unity" -- scenario validate .\Runtime Bridge for Unity\Scenarios\Examples\SpellCasting\prepare-magic-bolt.json
dotnet run --project ".\Runtime Bridge for Unity" -- scenario run .\Runtime Bridge for Unity\Scenarios\Examples\SpellCasting\prepare-magic-bolt.json
dotnet run --project ".\Runtime Bridge for Unity" -- scenario list .\Runtime Bridge for Unity\Scenarios\Examples
```

The formal schema and reusable examples are in
[`Runtime Bridge for Unity/Scenarios`](Runtime%20Bridge%20for%20Unity/Scenarios).
The Runner orchestrates and verifies; game rules and authorization remain in
Unity Commands and Probes.

## Protocol and safety boundary

- TCP is loopback-only; remote access is intentionally unsupported.
- Each connection carries one UTF-8 JSON Lines request and response.
- Frames are limited to 1 MiB and responses preserve correlation IDs.
- A timeout means the outcome is unknown; it does not prove rollback or failure.
- Graceful stop verifies PID, executable path, and process start time and refuses
  to kill a mismatched process.
- The protocol has no remote authentication, TLS, authorization, replay
  protection, or audit logging.
- Runtime commands `echo` and `smoke` are reserved by the bridge; product
  commands must use distinct names.

## Development

```powershell
dotnet build ".\Runtime Bridge for Unity.slnx" -c Release
dotnet run --project ".\tests\RuntimeBridge.Tests" -c Release --no-build
dotnet pack ".\Runtime Bridge for Unity\Runtime Bridge for Unity.csproj" -c Release
```

The host test harness uses a real fake Player process. Unity 6 package import
and Git URL installation were validated with Unity `6000.3.21f1`.

The repository name contains the existing `runtime-bride-for-unity` spelling;
the UPM package id intentionally uses the correctly spelled `runtime-bridge`.
