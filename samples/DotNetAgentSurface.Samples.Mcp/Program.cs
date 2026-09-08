using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using DotNetAgentSurface.Samples.TaskTracker;

var services = new SingleServiceProvider(new TaskTrackerService());
var catalog = OperationCatalog.Discover(typeof(TaskTrackerService));

// serverInstructions is optional guidance for MCP clients that honor
// McpServerOptions.ServerInstructions (typically folded into the model's system prompt).
var server = new McpOperationServer(
    new McpOperationAdapter(catalog, new OperationInvoker(services)),
    serverInstructions: "Use the task tracker tools to list, create, and complete tasks instead of tracking them in free text.");

await server.RunStdioAsync();

/// <summary>Resolves a single pre-built service instance, sufficient for a single-service sample host.</summary>
internal sealed class SingleServiceProvider(object service) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
}
