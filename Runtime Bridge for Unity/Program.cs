using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RuntimeBridge.Unity;
using RuntimeBridge.Unity.Cli;
using RuntimeBridge.Unity.Tools;

if (args.Length > 0 && !string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
{
    return await CliApplication.RunAsync(args);
}

var builder = Host.CreateApplicationBuilder(args.Length == 0 ? args : args[1..]);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<RuntimeSessionStore>();
builder.Services.AddSingleton<PlayerProcess>();
builder.Services.AddSingleton<RuntimeBridgeService>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RuntimeBridgeTools>();

await builder.Build().RunAsync();
return 0;
