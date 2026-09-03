using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class HangfireOperationCatalogBuilderExtensionsTests
{
    [Fact]
    public void AddHangfireRecurringOperations_does_not_access_storage_until_invocation()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringOperations(storage, manager)
            .Build();

        manager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());

        var list = Assert.Single(catalog.Operations, operation => operation.Name == "list-recurring-hangfire");
        Assert.Equal("Hangfire", list.Category);
        var trigger = Assert.Single(catalog.Operations, operation => operation.Name == "trigger-recurring-hangfire");
        Assert.Equal(AgentSafetyLevel.Confirm, trigger.SafetyLevel);
    }

    [Fact]
    public async Task ListRecurringHangfire_returns_current_jobs_in_ordinal_order()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(storage, manager).Build();
        manager.AddOrUpdate("zebra", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());
        manager.AddOrUpdate("Alpha", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Hourly());

        var result = await InvokeAsync(catalog, "list-recurring-hangfire");

        Assert.True(result.Succeeded);
        var jobs = Assert.IsAssignableFrom<IReadOnlyList<RecurringHangfireJobInfo>>(result.Value);
        Assert.Equal(["Alpha", "zebra"], jobs.Select(job => job.JobId));
        Assert.All(jobs, job => Assert.Equal(typeof(TestJobs).FullName, job.JobType));
    }

    [Fact]
    public async Task TriggerRecurringHangfire_rejects_unknown_job_without_triggering_manager()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecordingRecurringJobManager();
        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(storage, manager).Build();

        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", "missing");

        Assert.True(result.Succeeded);
        var acknowledgement = Assert.IsType<TriggerRecurringHangfireResult>(result.Value);
        Assert.Equal("rejected", acknowledgement.Status);
        Assert.Equal("missing", acknowledgement.JobId);
        Assert.Null(manager.TriggeredJobId);
    }

    [Fact]
    public async Task TriggerRecurringHangfire_uses_current_job_and_returns_acknowledgement()
    {
        using var storage = new InMemoryStorage();
        var storageManager = new RecurringJobManager(storage);
        storageManager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());
        var manager = new RecordingRecurringJobManager();
        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(storage, manager).Build();

        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", "NIGHTLY-CLEANUP");

        Assert.True(result.Succeeded);
        var acknowledgement = Assert.IsType<TriggerRecurringHangfireResult>(result.Value);
        Assert.Equal("enqueued", acknowledgement.Status);
        Assert.Equal("nightly-cleanup", acknowledgement.JobId);
        Assert.Equal("nightly-cleanup", manager.TriggeredJobId);
        Assert.Null(acknowledgement.EnqueueId);
    }

    [Fact]
    public async Task TriggerRecurringHangfire_isolated_execution_runs_to_completion_without_touching_configured_storage()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("nightly-cleanup", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringOperations(storage, manager, options =>
            {
                options.ExecutionModel = HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer;
                options.IsolatedExecutionTimeout = TimeSpan.FromSeconds(10);
            })
            .Build();

        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", "nightly-cleanup");

        Assert.True(result.Succeeded);
        var acknowledgement = Assert.IsType<TriggerRecurringHangfireResult>(result.Value);
        Assert.Equal("succeeded", acknowledgement.Status);
        Assert.Equal("nightly-cleanup", acknowledgement.JobId);
        Assert.NotNull(acknowledgement.EnqueueId);

        using var connection = storage.GetConnection();
        var recurringJobs = connection.GetRecurringJobs();
        var registeredJob = Assert.Single(recurringJobs);
        Assert.Null(registeredJob.LastJobId);
    }

    [Fact]
    public async Task TriggerRecurringHangfire_isolated_execution_reports_job_failure()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("failing-job", Job.FromExpression(() => TestJobs.Fail()), Cron.Daily());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringOperations(storage, manager, options =>
            {
                options.ExecutionModel = HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer;
                options.IsolatedExecutionTimeout = TimeSpan.FromSeconds(10);
            })
            .Build();

        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", "failing-job");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task TriggerRecurringHangfire_isolated_execution_times_out_for_long_running_jobs()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate("slow-job", Job.FromExpression(() => TestJobs.Sleep(1500)), Cron.Daily());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringOperations(storage, manager, options =>
            {
                options.ExecutionModel = HangfireExecutionModel.ExecuteUsingIsolatedInMemoryServer;
                options.IsolatedExecutionTimeout = TimeSpan.FromMilliseconds(200);
            })
            .Build();

        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", "slow-job");

        Assert.True(result.IsCancelled);
    }

    private static Task<OperationInvocationResult> InvokeAsync(OperationCatalog catalog, string operationName, string? jobId = null)
    {
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == operationName);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? inputs = jobId is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["jobId"] = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(jobId)).RootElement.Clone()
            };
        return new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs).AsTask();
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

        public static void Fail()
        {
            throw new InvalidOperationException("boom");
        }

        public static void Sleep(int milliseconds)
        {
            Thread.Sleep(milliseconds);
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
