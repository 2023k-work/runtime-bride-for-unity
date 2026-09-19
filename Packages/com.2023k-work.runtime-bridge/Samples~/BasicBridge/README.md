# Basic Bridge sample

Create a GameObject with `RuntimeBridgeUnity` and `ExampleRuntimeCommands`.
The sample registers `sample.echo` and `frame` handlers and keeps all handler
execution on the Unity main thread through the runtime bridge queue. `echo` and
`smoke` are reserved by the runtime bridge and cannot be replaced by product
handlers.
