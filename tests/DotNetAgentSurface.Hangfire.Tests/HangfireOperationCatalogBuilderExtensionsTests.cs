using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;

namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class HangfireOperationCatalogBuilderExtensionsTests
{
    [Fact]
    public void AddHangfireRecurringJobs_discovers_jobs_with_confirm_safety_by_default()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringJobs(storage, manager)
            .Build();

        var operation = Assert.Single(catalog.Operations);
        Assert.Equal("nightly-cleanup", operation.Name);
        Assert.Equal(AgentSafetyLevel.Confirm, operation.SafetyLevel);
        Assert.Equal("Hangfire", operation.Category);
        Assert.Contains("Triggers the 'nightly-cleanup' recurring job", operation.Description);
    }

    [Fact]
    public async Task AddHangfireRecurringJobs_invokes_the_bound_manager_trigger()
    {
        using var storage = new InMemoryStorage();
        var storageManager = new RecurringJobManager(storage);
        storageManager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());
        var recordingManager = new RecordingRecurringJobManager();
        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringJobs(storage, recordingManager)
            .Build();

        var result = await new OperationInvoker(new NullServiceProvider())
            .InvokeAsync(Assert.Single(catalog.Operations));

        Assert.True(result.Succeeded);
        Assert.Equal("nightly-cleanup", recordingManager.TriggeredJobId);
    }

    [Fact]
    public void AddHangfireRecurringJobs_allows_per_job_metadata_enrichment()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringJobs(storage, manager, options =>
            {
                options.Category = "Maintenance";
                options.Enrich = (job, registration) =>
                {
                    registration.Name = $"run-{job.Id}";
                    registration.Description = "Runs cleanup immediately.";
                    registration.SafetyLevel = AgentSafetyLevel.Dangerous;
                    registration.Aliases.Add("cleanup-now");
                };
            })
            .Build();

        var operation = Assert.Single(catalog.Operations);
        Assert.Equal("run-nightly-cleanup", operation.Name);
        Assert.Equal("Runs cleanup immediately.", operation.Description);
        Assert.Equal("Maintenance", operation.Category);
        Assert.Equal(AgentSafetyLevel.Dangerous, operation.SafetyLevel);
        Assert.Contains("cleanup-now", operation.Aliases);
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public string? TriggeredJobId { get; private set; }

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
        }

        public void Trigger(string recurringJobId)
        {
            TriggeredJobId = recurringJobId;
        }

        public void RemoveIfExists(string recurringJobId)
        {
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static class TestJobs
    {
        public static void CleanUp()
        {
        }
    }
}
