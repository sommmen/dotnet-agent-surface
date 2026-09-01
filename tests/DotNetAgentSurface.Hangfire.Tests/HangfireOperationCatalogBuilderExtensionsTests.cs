using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;

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

    [Fact]
    public void AddHangfireJobTypes_discovers_matching_job_types_without_registering_anything()
    {
        var client = new RecordingBackgroundJobClient();

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobTypes(
                client,
                [typeof(ParameterlessDiscoveredJob).Assembly],
                type => typeof(IParameterlessDiscoveredJob).IsAssignableFrom(type),
                type => type.GetMethod(nameof(ParameterlessDiscoveredJob.Execute))!)
            .Build();

        var operation = Assert.Single(catalog.Operations);
        var expectedName = typeof(ParameterlessDiscoveredJob).FullName!;

        Assert.Equal(expectedName, operation.Name);
        Assert.Contains("Enqueues a background execution", operation.Description);
        Assert.Empty(client.CreatedJobs);
    }

    [Fact]
    public async Task AddHangfireJobTypes_enqueues_the_job_and_allows_metadata_enrichment()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobTypes(
                client,
                [typeof(ParameterlessDiscoveredJob).Assembly],
                type => typeof(IParameterlessDiscoveredJob).IsAssignableFrom(type),
                type => type.GetMethod(nameof(ParameterlessDiscoveredJob.Execute))!,
                configure: options =>
                {
                    options.OperationNameFactory = type => $"job:{type.Name}";
                    options.Category = "Maintenance";
                    options.Enrich = (_, registration) =>
                    {
                        registration.Description = "Runs discovered cleanup immediately.";
                        registration.SafetyLevel = AgentSafetyLevel.Dangerous;
                        registration.Aliases.Add("discovered-cleanup");
                    };
                })
            .Build();

        var operation = Assert.Single(catalog.Operations);
        Assert.Empty(client.CreatedJobs);

        var result = await new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation);

        Assert.True(result.Succeeded);
        Assert.Equal("job:ParameterlessDiscoveredJob", operation.Name);
        var created = Assert.Single(client.CreatedJobs);
        Assert.Equal(typeof(ParameterlessDiscoveredJob), created.Job.Type);
        Assert.Equal(nameof(ParameterlessDiscoveredJob.Execute), created.Job.Method.Name);
        Assert.Empty(created.Job.Args);
        Assert.IsType<EnqueuedState>(created.State);
        Assert.Equal("Runs discovered cleanup immediately.", operation.Description);
        Assert.Equal("Maintenance", operation.Category);
        Assert.Equal(AgentSafetyLevel.Dangerous, operation.SafetyLevel);
        Assert.Contains("discovered-cleanup", operation.Aliases);
    }

    [Fact]
    public async Task AddHangfireJobTypes_supports_options_based_jobs_only_when_arguments_are_supplied()
    {
        var client = new RecordingBackgroundJobClient();
        var jobOptions = new DiscoveredJobOptions { BatchSize = 25 };

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobTypes(
                client,
                [typeof(OptionsDiscoveredJob).Assembly],
                type => typeof(IOptionsDiscoveredJob).IsAssignableFrom(type),
                type => type.GetMethod(nameof(OptionsDiscoveredJob.Execute))!,
                _ => [jobOptions])
            .Build();

        var operation = Assert.Single(catalog.Operations);
        await new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation);

        var created = Assert.Single(client.CreatedJobs);
        Assert.Equal(typeof(OptionsDiscoveredJob), created.Job.Type);
        Assert.Same(jobOptions, Assert.Single(created.Job.Args));
    }

    [Fact]
    public void AddHangfireJobTypes_excludes_abstract_types_even_when_the_predicate_matches()
    {
        var client = new RecordingBackgroundJobClient();

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobTypes(
                client,
                [typeof(AbstractParameterlessDiscoveredJob).Assembly],
                type => type == typeof(AbstractParameterlessDiscoveredJob),
                type => type.GetMethod(nameof(AbstractParameterlessDiscoveredJob.Execute))!)
            .Build();

        Assert.Empty(catalog.Operations);
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public string? TriggeredJobId { get; private set; }

        public List<(string Id, Job Job, string CronExpression)> AddedJobs { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
            AddedJobs.Add((recurringJobId, job, cronExpression));
        }

        public void Trigger(string recurringJobId)
        {
            TriggeredJobId = recurringJobId;
        }

        public void RemoveIfExists(string recurringJobId)
        {
        }
    }

    private sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> CreatedJobs { get; } = [];

        public string Create(Job job, IState state)
        {
            CreatedJobs.Add((job, state));
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string? expectedState) => true;
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

    private interface IParameterlessDiscoveredJob
    {
        void Execute();
    }

    private sealed class ParameterlessDiscoveredJob : IParameterlessDiscoveredJob
    {
        public void Execute()
        {
        }
    }

    private abstract class AbstractParameterlessDiscoveredJob : IParameterlessDiscoveredJob
    {
        public void Execute()
        {
        }
    }

    private sealed class DiscoveredJobOptions
    {
        public int BatchSize { get; set; }
    }

    private interface IOptionsDiscoveredJob
    {
        void Execute(DiscoveredJobOptions options);
    }

    private sealed class OptionsDiscoveredJob : IOptionsDiscoveredJob
    {
        public void Execute(DiscoveredJobOptions options)
        {
        }
    }
}
