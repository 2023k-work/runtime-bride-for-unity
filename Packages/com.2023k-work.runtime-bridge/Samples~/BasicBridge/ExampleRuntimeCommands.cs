using Newtonsoft.Json.Linq;
using RuntimeBridge.Unity;
using UnityEngine;

namespace RuntimeBridge.Unity.Samples
{
    public sealed class ExampleRuntimeCommands : MonoBehaviour
    {
        [SerializeField] private RuntimeBridgeUnity bridge;

        private void Awake()
        {
            if (bridge == null) bridge = GetComponent<RuntimeBridgeUnity>();
            bridge.RegisterCommand("sample.echo", payload => payload ?? new JObject());
            bridge.RegisterCommand("frame", _ => new JObject { ["frame"] = Time.frameCount });
        }
    }
}
