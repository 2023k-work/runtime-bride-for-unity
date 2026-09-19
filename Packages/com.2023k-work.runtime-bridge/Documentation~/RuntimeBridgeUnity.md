# RuntimeBridgeUnity

`RuntimeBridgeUnity` is the Unity Player-side endpoint used by the companion
CLI/MCP host. It is intentionally a runtime-only component and does not depend
on `UnityEditor`. At startup it enables `Application.runInBackground` so the
`Update()` queue continues to drain when the Player loses focus.

## Wire operations

- `ping`: liveness response from the network worker.
- `hello`: Player identity and Unity version.
- `ready`: Player readiness on the Unity main thread.
- `status`: runtime state and frame metadata.
- `command`: invokes a registered command on the Unity main thread.
- `shutdown`: acknowledges, then calls `Application.Quit()` from `Update()`.

Command handlers should treat payloads as untrusted input and return a stable
JSON value. They should not perform blocking network or file operations on the
Unity main thread. The built-in `echo` and `smoke` command names are reserved;
product handlers must register distinct names.
