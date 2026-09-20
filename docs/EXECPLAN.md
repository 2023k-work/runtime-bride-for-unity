# Runtime Bridge for Unity — migration ExecPlan

## Goal

Turn this repository into a Unity Player runtime bridge that exposes the same
authoritative Player operations through both a human/CI CLI and an MCP stdio
server. Reuse proven pieces from `C:\Users\User\Desktop\CLI Tool` without
retaining the source project's Luna-specific product identity.

## Constraints and invariants

- Unity Player owns Unity state and executes Unity API calls on its main thread.
- The desktop host is a client; MCP does not become a second state authority.
- Player transport remains loopback-only unless a separate authenticated remote
  protocol is designed.
- Timeouts mean outcome unknown, not command rollback or command failure.
- Player lifecycle operations must verify session and process identity before
  shutdown; they must not kill an unverified process.
- MCP protocol output owns stdout. Diagnostics and CLI/MCP operational logging
  must not corrupt stdio framing.
- Migration is additive and reversible: the source repository is read-only and
  remains the rollback reference.

## Survey batches

### Batch 1 — target skeleton and source host/CLI (read 2026-09-19)

Raw observations:

- Target contains one .NET 10 MCP template executable, a sample random-number
  tool, package metadata placeholders, and no tests or Unity runtime component.
- Target root currently has no detected Git metadata/status output.
- Source has three explicit areas: `src/Luna.UnityCli`, `tests`, and `unity`.
- Source CLI targets .NET 8 with warnings-as-errors and no third-party package.
- Source `Program` combines JSON output, command dispatch, lifecycle orchestration,
  smoke-test orchestration, and exception-to-exit-code mapping.
- Source `CliOptions` is a handwritten parser for lifecycle, raw protocol, and
  smoke-test commands.
- Source README documents critical behavioral contracts: loopback JSON Lines,
  one request per connection, 1 MiB frames, correlation IDs, session checking,
  main-thread command execution, process identity verification, and timeout as
  unknown outcome.
- Target MCP host currently assumes every invocation is MCP stdio and therefore
  cannot yet coexist with a conventional CLI entry point.

Open conclusions (deferred until remaining batches are read):

- Whether to keep one executable with mode dispatch or split CLI and MCP hosts.
- Which source types can move unchanged versus need product-neutral renaming.
- Whether the Unity component's protocol DTOs duplicate host DTOs or intentionally
  mirror them across a runtime/compiler boundary.

### Batch 2 — protocol and runtime lifecycle

Raw observations:

- `LunaProtocol` owns JSON settings plus request/response/error DTOs. JSON names
  are product-neutral despite Luna-prefixed CLR type names.
- `LunaClient` owns one-request-per-TCP-connection framing, 1 MiB request and
  response limits, per-request correlation IDs, timeout conversion, and remote
  error conversion. This is a cohesive reusable protocol adapter.
- `RuntimeSessionStore` persists one JSON file per session via temp-file replace.
  Its default directory and environment variable are Luna-specific and need
  replacement; persistence shape and validation are reusable.
- `ProcessIdentity` checks PID, full executable path, and process start time.
  This is the guard that prevents stale state from targeting a reused PID.
- `PlayerProcess` owns launch arguments, readiness polling, graceful shutdown,
  and session updates. Product-specific argument names/log names are interwoven
  but localized.
- Launch cleanup uses forced process termination only when the host launched a
  process but then failed before it could persist its identity. Normal stop
  deliberately never kills the Player.
- The client supports arbitrary hosts even though the documented trust model is
  loopback-only. The migrated public surface should default to loopback and make
  non-loopback use an explicit future decision rather than accidental support.
- Host-facing lifecycle operations and raw command calls currently instantiate
  clients directly; a shared application service is needed so CLI and MCP do not
  duplicate orchestration or disagree on error semantics.

### Batch 3 — Unity bridge and tests

Raw observations:

- Unity bridge binds `IPAddress.Loopback` and places all operations except the
  constant `ping` response onto a queue drained by `Update()`. Command handlers,
  Unity API reads, and `Application.Quit()` therefore remain on the main thread.
- Unity bridge intentionally mirrors wire DTOs using Newtonsoft types. Sharing
  the host assembly would couple Unity's compiler/runtime and JSON dependency to
  the .NET host; keep this as a protocol mirror and test interoperability.
- The bridge has built-in `echo` and `smoke` handlers plus an extension point for
  product runtime commands. It reports stable error codes.
- Fake Player implements the same loopback protocol without Unity. It is the
  appropriate executable characterization seam for lifecycle and MCP/CLI host
  integration, but it does not establish real Unity compatibility.
- Existing tests cover correlation, payloads, timeouts, endpoint validation,
  session mismatch, two simultaneous Players, ready timeout, graceful shutdown,
  smoke results, and log metadata.
- Existing tests are a custom executable harness rather than a test framework;
  this avoids new packages and can be migrated while adding MCP-facing service
  tests.

Survey conclusion:

- Use one packaged executable with explicit mode dispatch: no arguments or
  `mcp` starts stdio MCP; any CLI command starts CLI mode. This preserves clean
  MCP stdout while retaining direct CLI use.
- Create one `RuntimeBridgeService` as the application boundary. CLI and MCP
  adapters receive the same lifecycle, session, protocol, and timeout semantics.
- Rename product identity and launch switches to `runtime-bridge-*`; no legacy
  compatibility alias is required because the target is a new repository.
- Preserve the protocol/lifecycle core and fake-player characterization tests;
  do not copy the Luna smoke-result feature until it has a product-neutral test
  contract.
- Keep runtime state authority in Unity Player; stored session files are launch
  identity and recovery metadata, not authoritative Unity state.

## Planned slices

1. Complete architecture and duplication survey; record target boundaries. — done
2. Introduce product-neutral protocol/runtime core with characterization tests. — done
3. Add CLI mode while preserving MCP stdio ownership. — done
4. Add MCP tools as thin adapters over the same application service. — done
5. Add the Unity runtime component and end-to-end fake-player tests. — done
6. Replace the template packaging/docs and run build/test/package checks. — done
7. Package the Unity runtime component as a Unity 6 UPM Git package and verify
   it with the Unity 6.3.21f1 Editor. — done
8. Add the versioned Runtime Scenario Runner v1, shared by CLI and MCP, with
   multi-instance lifecycle, Command/Probe orchestration, generic assertions,
   cleanup, and evidence reports. — done

## Verification record

- `dotnet build ".\Runtime Bridge for Unity.slnx" -c Release` — passed,
  0 warnings and 0 errors.
- `dotnet run --project ".\tests\RuntimeBridge.Tests\RuntimeBridge.Tests.csproj"
  -c Release --no-build` — passed, 4/4 tests.
- CLI `--help` smoke invocation — passed and emitted JSON.
- `dotnet pack ".\Runtime Bridge for Unity\Runtime Bridge for Unity.csproj"
  -c Release` — passed for the aggregate package and all six declared RIDs.
- Residue scan found source-product names only in this survey record; no product
  identity remained in runtime source, package metadata, or user documentation.
- Not run: real Unity Player compilation/execution and external MCP client
  negotiation.

### Runtime Scenario Runner v1 verification

- Host Release build with `UseAppHost=false`/framework-dependent output — passed,
  0 warnings and 0 errors.
- Fake Player and custom host harness builds — passed.
- Host regression and Scenario Runner integration — passed, 8/8 tests.
- All three packaged examples — passed `scenario validate`.
- `scenario list` — passed and returned all three examples while excluding the
  schema document.
- Not run: the examples against a real Unity 6 gameplay build; the example
  command and Probe names remain feature-owned integration contracts.

### Unity package verification

- Package installed through `UnityEditor.PackageManager.Client.Add` from a local
  package path — passed, resolved `com.unity.nuget.newtonsoft-json` 3.2.2.
- Unity 6.3.21f1 batchmode import/compile verification — passed.
- Initial GitHub package commit `8418853` and tag `v0.1.0` — pushed
  successfully; the tag remains immutable.
- Remediation commit `1b0e8df` and tag `v0.1.1` — pushed successfully.
- GitHub URL/tag installation — passed; UPM reported `source=Git`, version
  `0.1.0`, and imported `RuntimeBridge.Unity` successfully.
- GitHub URL/tag installation for `v0.1.1` — passed; UPM reported
  `source=Git`, version `0.1.1`, and imported/compiled `RuntimeBridge.Unity`
  successfully.
- First push attempt was rejected because the connected OAuth credential lacks
  GitHub `workflow` scope. The workflow file was excluded from the release
  commit; package and host validation remain unaffected.

### Release gate remediation — 0.1.1

The Unity validation report showed that the original BasicBridge configuration
could answer worker-thread `ping`, but lost focus stopped the Player update loop;
therefore queued `ready`, command, and `shutdown` requests timed out unless the
test harness separately enabled background execution. This was a release-blocking
configuration defect, not a transport failure.

- `RuntimeBridgeUnity.Awake()` now sets `Application.runInBackground = true`,
  making the bridge's queued-operation contract independent of Player focus.
- `echo` and `smoke` are reserved bridge commands. Product handlers must use
  distinct names; the BasicBridge sample now registers `sample.echo`.
- The package version is `0.1.1`; the existing `v0.1.0` tag remains immutable.

Verification for this remediation:

- Host regression: 4/4 passed.
- Unity 6.3.21f1 local package import/compile: passed (`Local version=0.1.1`).
- Windows Player build with project `runInBackground=0`: passed.
- Focused Player lifecycle rerun with no harness background override: passed;
  `ready`, `sample.echo`, built-in `echo`, and `shutdown` all returned success,
  and the Player exited after shutdown.
- Main-thread probe: passed with `mainThreadId=1`, `commandThreadId=1`.

The broader P1 stress/malformed-input/platform matrix and LIFE-06 scene unload
case remain outside this remediation run. The release gate can close the two
reported P0 blockers, but those deferred cases still require the Unity team's
planned test pass.

## Explicit exclusions

- Unity Editor control.
- Remote-host transport, authentication, TLS, or authorization.
- Production recipe/game-specific command handlers.
- Claiming real Unity Player validation without an actual Unity build.
