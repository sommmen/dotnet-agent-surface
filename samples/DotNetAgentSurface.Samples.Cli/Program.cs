using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Samples.TaskTracker;

var services = new SingleServiceProvider(new TaskTrackerService());
var catalog = OperationCatalog.Discover(typeof(TaskTrackerService));
var adapter = new OperationCommandLineAdapter(catalog, new OperationInvoker(services));

var result = await adapter.ExecuteAsync(args);
if (!string.IsNullOrEmpty(result.Output))
{
    Console.WriteLine(result.Output);
}

if (!string.IsNullOrEmpty(result.Error))
{
    Console.Error.WriteLine(result.Error);
}

return result.ExitCode;

/// <summary>Resolves a single pre-built service instance, sufficient for a single-service sample host.</summary>
internal sealed class SingleServiceProvider(object service) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
}
