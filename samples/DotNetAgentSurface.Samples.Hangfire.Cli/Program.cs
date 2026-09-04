using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;

using var storage = new InMemoryStorage();
var jobManager = new RecurringJobManager(storage);
jobManager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => SampleJobs.CleanUp()), Cron.Daily());
jobManager.AddOrUpdate("hourly-report", Job.FromExpression(() => SampleJobs.SendReport()), Cron.Hourly());

var catalog = new OperationCatalogBuilder()
    .AddHangfireRecurringOperations(storage, jobManager)
    .Build();
var invoker = new OperationInvoker(
    new NullServiceProvider(),
    policies: [new DangerousOperationConfirmationPolicy()]);
var adapter = new OperationCommandLineAdapter(catalog, invoker);

var result = SkillGeneratorCommand.CanHandle(args)
    ? await SkillGeneratorCommand.ExecuteAsync(args, catalog, outputDirectoryDefault: "skill")
    : await adapter.ExecuteAsync(args);

if (!string.IsNullOrEmpty(result.Output))
{
    Console.Out.WriteLine(result.Output);
}

if (!string.IsNullOrEmpty(result.Error))
{
    Console.Error.WriteLine(result.Error);
}

return result.ExitCode;

internal static class SampleJobs
{
    public static void CleanUp()
    {
    }

    public static void SendReport()
    {
    }
}

internal sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
