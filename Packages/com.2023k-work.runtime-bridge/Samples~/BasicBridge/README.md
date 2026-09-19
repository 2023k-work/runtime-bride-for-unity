# Basic Bridge sample

Create a GameObject with `RuntimeBridgeUnity` and `ExampleRuntimeCommands`.
The sample registers `echo` and `frame` handlers and keeps all handler
execution on the Unity main thread through the runtime bridge queue.
