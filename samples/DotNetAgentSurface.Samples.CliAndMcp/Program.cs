using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Mcp;
using DotNetAgentSurface.Samples.TaskTracker;

// This sample demonstrates that CLI and MCP are not mutually exclusive hosting choices: the same
// executable, built from the same catalog/invoker construction code, can serve either surface. The
// mode is selected by the first argument rather than by picking a different project/executable, as
// DotNetAgentSurface.Samples.Cli and DotNetAgentSurface.Samples.Mcp do separately.
//
// Both surfaces still cannot be *simultaneously active* within a single process invocation: MCP's
// stdio transport reserves stdout exclusively for JSON-RPC protocol traffic (see
// McpOperationServer.RunStdioAsync), so mixing it with normal CLI stdout writes in the same process
// at the same time would corrupt the protocol stream. Selecting the mode up front - while sharing one
// catalog/invoker - is what lets one host support both without that conflict.
var services = new SingleServiceProvider(new TaskTrackerService());
var catalog = OperationCatalog.Discover(typeof(TaskTrackerService));
var invoker = new OperationInvoker(services);

if (args is ["mcp", ..])
{
    var server = new McpOperationServer(new McpOperationAdapter(catalog, invoker));
    await server.RunStdioAsync();
    return 0;
}

var adapter = new OperationCommandLineAdapter(catalog, invoker, new ToonAgentOutputRenderer());

// "generate"/"check" are dispatched to the standalone skill reference command surface before falling
// through to the operation adapter, mirroring DotNetAgentSurface.Samples.Cli.
var result = SkillGeneratorCommand.CanHandle(args)
    ? await SkillGeneratorCommand.ExecuteAsync(args, catalog, outputDirectoryDefault: "skill")
    : await adapter.ExecuteAsync(args);

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
