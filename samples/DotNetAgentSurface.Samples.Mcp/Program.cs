using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using DotNetAgentSurface.Samples.TaskTracker;

var services = new SingleServiceProvider(new TaskTrackerService());
var catalog = OperationCatalog.Discover(typeof(TaskTrackerService));
var server = new McpOperationServer(new McpOperationAdapter(catalog, new OperationInvoker(services)));

await server.RunStdioAsync();

/// <summary>Resolves a single pre-built service instance, sufficient for a single-service sample host.</summary>
internal sealed class SingleServiceProvider(object service) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
}
