using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hosting;
using DotNetAgentSurface.Samples.CliAndMcp;
using DotNetAgentSurface.Samples.TaskTracker;
using Microsoft.Extensions.DependencyInjection;

// One executable, one dependency-injection composition, two surfaces:
//   tasktracker                       runs the MCP server over stdio
//   tasktracker cli <operation> ...   runs the plain command-line interface
// Both surfaces share the registrations below; a CLI invocation never starts hosted services
// and keeps stdout for the result only (logging goes to stderr).
return await AgentSurfaceApplication.RunAsync(args, app =>
{
    app.ExecutableName = "tasktracker";
    app.SkillDescription = "Manage the task tracker through its generated CLI operations.";

    app.ConfigureServices(builder =>
    {
        builder.Services.AddSingleton<TaskTrackerService>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton<ReportOperations>();
    });

    app.ConfigureMcp(mcp => mcp.WithStdioServerTransport().WithAgentSurfaceCatalog());

    app.ConfigureCatalog(_ => OperationCatalog.Discover(
        XmlOperationDocumentationSource.ProbeAppBaseDirectory(),
        typeof(TaskTrackerService),
        typeof(ReportOperations)));

    app.AddGlobalOption("--audit-name", injectAs: "auditName", description: "Label recorded by operations that audit their work.");
    app.AddVerb("version", _ =>
    {
        Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version);
        return 0;
    });
});
