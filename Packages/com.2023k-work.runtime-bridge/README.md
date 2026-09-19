# Runtime Bridge for Unity

Unity 6 runtime component for a local MCP/CLI bridge.

The package binds only to `127.0.0.1`. It accepts one UTF-8 JSON Lines request
per TCP connection and queues Unity operations for `Update()`, so Unity APIs and
registered command handlers execute on the Unity main thread.

## Install from GitHub

In Unity Package Manager, choose **Add package from git URL** and use:

```text
https://github.com/2023k-work/runtime-bride-for-unity.git?path=Packages/com.2023k-work.runtime-bridge#v0.1.0
```

The package requires Unity 6 and declares its Newtonsoft JSON dependency in
`package.json`.

## Setup

1. Add `RuntimeBridgeUnity` to a GameObject in the Player startup scene.
2. Launch the Player with `--runtime-bridge-session <id>` and
   `--runtime-bridge-port <port>`.
3. Register product commands from a component in the same scene:

```csharp
using Newtonsoft.Json.Linq;
using RuntimeBridge.Unity;
using UnityEngine;

public sealed class ExampleRuntimeCommands : MonoBehaviour
{
    [SerializeField] private RuntimeBridgeUnity bridge;

    private void Awake()
    {
        bridge.RegisterCommand("echo", payload => payload ?? new JObject());
    }
}
```

`ping` is answered by the network worker. `hello`, `ready`, `status`,
`command`, and `shutdown` are completed by the Unity main thread. A session
mismatch returns `SESSION_MISMATCH`; an unknown command returns
`UNKNOWN_COMMAND`.

This package is local-development infrastructure. It does not provide remote
authentication, TLS, authorization, replay protection, or audit logging.
