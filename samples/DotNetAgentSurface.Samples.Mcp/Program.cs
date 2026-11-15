using DotNetAgentSurface.Core;
using DotNetAgentSurface.Samples.TaskTracker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Hosts the catalog on the official ModelContextProtocol SDK builder: WithOperations registers every
// operation as an SDK tool, so it composes with WithStdioServerTransport(), WithTools<T>(), prompts,
// resources and hosted services. McpOperationServer remains the zero-DI alternative.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<TaskTrackerService>();
builder.Services
    .AddMcpServer(options => options.ServerInstructions =
        "Use the task tracker tools to list, create, and complete tasks instead of tracking them in free text.")
    .WithStdioServerTransport()
    .WithOperations(catalog => catalog.AddFromType<TaskTrackerService>());

await builder.Build().RunAsync();
