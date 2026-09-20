# Runtime Bridge for Unity — architecture map

## Topology and authority

```text
MCP client ──stdio──┐
                   ├─ host adapters ─ RuntimeBridgeService ─ loopback JSONL ─ Unity Player
human / CI ──CLI────┘                         │                         (state authority)
scenario JSON ── ScenarioRunner ──────────────┘
                                             └─ session identity files
                                                (recovery metadata only)
```

- `Program.cs` owns process-mode selection and MCP host composition.
- `Cli/CliApplication.cs` and `Tools/RuntimeBridgeTools.cs` are presentation adapters.
- `Runtime/RuntimeBridgeService.cs` is the shared use-case boundary.
- `Scenarios/RuntimeScenarioRunner.cs` is the host-side orchestration boundary for
  versioned test documents. It sequences lifecycle, Commands, Probes, assertions,
  cleanup, and evidence; it does not own game state or rules.
- `Scenarios/ScenarioValidator.cs` owns the v1 document contract and reference
  checks. `Scenarios/ScenarioAssertions.cs` owns generic value comparison.
- `Runtime/PlayerProcess.cs` owns launch, readiness polling, and verified graceful stop.
- `Runtime/RuntimeSession.cs` owns launch metadata and process identity verification.
- `Protocol/BridgeClient.cs` owns loopback framing, correlation, limits, and timeouts.
- `Packages/com.2023k-work.runtime-bridge/Runtime/RuntimeBridgeUnity.cs` owns
  authoritative Unity state and main-thread dispatch.
- `tests/RuntimeBridge.FakePlayer` is the executable protocol/lifecycle test seam.

## Stable contracts

- Default/no-argument mode is MCP stdio; stdout must contain only MCP messages.
- CLI output is JSON with stable failure categories.
- Transport is numeric loopback only, one request per connection, maximum 1 MiB.
- Player commands run on Unity's main thread.
- Normal stop never force-kills. Only launch rollback may kill the exact process
  that was just created but could not be recorded.
- A managed scenario instance is stopped by the Runner even when readiness,
  action, assertion, or cleanup fails. An attached instance is never stopped by
  the Runner because its process is outside this invocation's ownership.
- `TIMEOUT_UNKNOWN`, connection, and protocol outcomes are tool `ERROR` evidence;
  expected bridge errors are assertion results; unavailable externally attached
  Players are `BLOCKED`. None is silently reported as a gameplay failure.

## Duplication survey

Host and Unity DTOs intentionally mirror the wire contract but are not merge
candidates: the host uses System.Text.Json/.NET 10 while Unity uses Newtonsoft
and Unity's compiler/runtime. Interoperability tests control their drift.

No other duplicated client, session store, identity check, or lifecycle
orchestrator exists in the target. CLI and MCP both use `RuntimeBridgeService`.

## Naming findings

- `*Tools`: MCP adapter; `*Service`: use-case boundary; `*Client`: outbound
  transport; `*Store`: local persistence.
- The Unity component is outside the host namespace because it is copied into and
  compiled by a separate Unity project.

## Not yet verified

- Compilation and behavior inside a real Unity 6 Player.
- MCP negotiation from an external MCP client/inspector.
- Non-Windows publish artifacts and package installation through `dnx`.
- Crash/restart reconciliation during a mutating command.
