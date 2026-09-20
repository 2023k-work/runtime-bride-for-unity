# Runtime Scenario Runner v1

Runtime Scenarios are versioned JSON documents executed by the host. The
Runner only orchestrates Player lifecycle, calls registered Commands/Probes,
evaluates assertions, and writes evidence. It does not implement game rules.

## CLI

```powershell
dotnet run --project ".\Runtime Bridge for Unity" -- scenario validate .\Scenarios\Examples\PlayerMovement\basic-move.json
dotnet run --project ".\Runtime Bridge for Unity" -- scenario run .\Scenarios\Examples\SpellCasting\prepare-magic-bolt.json
dotnet run --project ".\Runtime Bridge for Unity" -- scenario run .\Scenarios\Examples\SpellCasting --output-dir .\TestResults
dotnet run --project ".\Runtime Bridge for Unity" -- scenario list .\Scenarios\Examples
```

`validate` never starts a Player. `run` starts every instance that has an
`executable`; an instance without an executable attaches to the declared
loopback `port` and optional `session`, and is not stopped during cleanup.

## Document contract

The formal schema is `Schema/runtime-scenario-v1.schema.json`. A scenario has
`preconditions`, `setup`, `actions`, `assertions`, and `cleanup` phases. Every
step has a unique `id`. The Runner executes phases in order and always attempts
cleanup, including after a timeout or assertion failure.

Supported step kinds:

- `command`: calls the existing Unity `command` API.
- `probe`: calls `runtime.probe` by default, or the explicit `probeCommand`,
  with `{ "probe", "player", "args" }` payload.
- `wait`: polls a Probe until its condition matches; it does not use a fixed
  sleep as the success mechanism.
- `assert`: compares a previous step value using `equals`, `notEquals`,
  numeric comparisons, `changedFrom`, `unchangedFrom`, `exists`,
  `notExists`, or `contains`.
- `manual`: records `MANUAL_REQUIRED` and stops the scenario before cleanup.

Command errors can be asserted with `expectedErrorCode`. An unexpected bridge
error is a behavior `FAIL`; a timeout, connection failure, or protocol failure
is a tool `ERROR` and is not reported as a gameplay assertion failure.

## Evidence

Each run receives a unique directory:

```text
TestResults/<scenario-name>/<run-id>/
  result.json
  commands.jsonl
  probes.jsonl
  host.log
  client.log                 # when an instance is named client
  instances/<instance>.log
```

`result.json` records scenario/version, Git commit, build artifacts, every step
status and duration, expected/actual values, failure codes, evidence path, and
the final `PASS`, `FAIL`, `BLOCKED`, `ERROR`, or `MANUAL_REQUIRED` status.
