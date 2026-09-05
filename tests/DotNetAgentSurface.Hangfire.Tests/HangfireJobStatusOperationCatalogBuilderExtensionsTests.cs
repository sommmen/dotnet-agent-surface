using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class HangfireJobStatusOperationCatalogBuilderExtensionsTests
{
    [Fact]
    public void AddHangfireJobStatusOperations_registers_both_operations_with_expected_defaults()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job)
            .Build();

        var continuation = Assert.Single(catalog.Operations, operation => operation.Name == "continue-hangfire-job");
        Assert.Equal("Hangfire", continuation.Category);
        Assert.Equal(AgentSafetyLevel.Confirm, continuation.SafetyLevel);

        var status = Assert.Single(catalog.Operations, operation => operation.Name == "get-hangfire-job-status");
        Assert.Equal("Hangfire", status.Category);
        Assert.Equal(AgentSafetyLevel.Safe, status.SafetyLevel);
    }

    [Fact]
    public void AddHangfireJobStatusOperations_throws_when_status_operation_name_is_blank_but_non_null()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            new OperationCatalogBuilder()
                .AddHangfireJobStatusOperations(client, storage, job, options =>
                {
                    options.StatusOperationName = "   "; // Blank but non-null
                })
                .Build();
        });

        Assert.Equal("configure", exception.ParamName);
        Assert.Contains("must not be blank", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddHangfireJobStatusOperations_skips_status_operation_when_name_is_null()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job, options =>
            {
                options.StatusOperationName = null;
            })
            .Build();

        var continuation = Assert.Single(catalog.Operations, operation => operation.Name == "continue-hangfire-job");
        Assert.Equal("Hangfire", continuation.Category);
        Assert.DoesNotContain(catalog.Operations, operation => operation.Name == "get-hangfire-job-status");
    }

    [Fact]
    public async Task ContinueHangfireJob_creates_a_job_awaiting_the_parent_job()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var parentJobId = client.Create(Job.FromExpression(() => TestJobs.CleanUp()), new EnqueuedState());
        var continuationJob = Job.FromExpression(() => TestJobs.CleanUp());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, continuationJob)
            .Build();

        var result = await InvokeAsync(catalog, "continue-hangfire-job", parentJobId);

        Assert.True(result.Succeeded);
        var continuationJobId = Assert.IsType<string>(result.Value);
        Assert.NotEqual(parentJobId, continuationJobId);

        using var connection = storage.GetConnection();
        var jobData = connection.GetJobData(continuationJobId);
        Assert.NotNull(jobData);
        Assert.Equal("Awaiting", jobData!.State);
    }

    [Fact]
    public async Task ContinueHangfireJob_rejects_a_missing_parent_job_id()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var continuationJob = Job.FromExpression(() => TestJobs.CleanUp());
        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, continuationJob)
            .Build();

        var result = await InvokeAsync(catalog, "continue-hangfire-job", parentJobId: "   ");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ContinueHangfireJob_honors_configured_continuation_options_and_next_state()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var parentJobId = client.Create(Job.FromExpression(() => TestJobs.CleanUp()), new EnqueuedState());
        var continuationJob = Job.FromExpression(() => TestJobs.CleanUp());

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, continuationJob, options =>
            {
                options.ContinuationOptions = JobContinuationOptions.OnAnyFinishedState;
            })
            .Build();

        var result = await InvokeAsync(catalog, "continue-hangfire-job", parentJobId);

        Assert.True(result.Succeeded);
        var continuationJobId = Assert.IsType<string>(result.Value);
        using var connection = storage.GetConnection();
        Assert.Equal("Awaiting", connection.GetJobData(continuationJobId)!.State);
    }

    [Fact]
    public async Task GetHangfireJobStatus_returns_the_current_state_for_a_known_job()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var jobId = client.Create(Job.FromExpression(() => TestJobs.CleanUp()), new EnqueuedState());
        var job = Job.FromExpression(() => TestJobs.CleanUp());
        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job)
            .Build();

        var result = await InvokeAsync(catalog, "get-hangfire-job-status", jobId);

        Assert.True(result.Succeeded);
        var status = Assert.IsType<HangfireJobStatus>(result.Value);
        Assert.Equal(jobId, status.JobId);
        Assert.Equal("Enqueued", status.State);
        Assert.Equal(typeof(TestJobs).FullName, status.JobType);
        Assert.Equal(nameof(TestJobs.CleanUp), status.Method);
        Assert.Null(status.DashboardUrl);
    }

    [Fact]
    public async Task GetHangfireJobStatus_returns_null_for_an_unknown_job()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());
        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job)
            .Build();

        var result = await InvokeAsync(catalog, "get-hangfire-job-status", "does-not-exist");

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task GetHangfireJobStatus_rejects_a_missing_job_id()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());
        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job)
            .Build();

        var result = await InvokeAsync(catalog, "get-hangfire-job-status", parentJobId: "   ");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task GetHangfireJobStatus_builds_a_dashboard_url_when_a_base_url_is_configured()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var jobId = client.Create(Job.FromExpression(() => TestJobs.CleanUp()), new EnqueuedState());
        var job = Job.FromExpression(() => TestJobs.CleanUp());
        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job, options =>
            {
                options.DashboardBaseUrl = "https://ops.example.com/hangfire/";
            })
            .Build();

        var result = await InvokeAsync(catalog, "get-hangfire-job-status", jobId);

        Assert.True(result.Succeeded);
        var status = Assert.IsType<HangfireJobStatus>(result.Value);
        Assert.Equal($"https://ops.example.com/hangfire/jobs/details/{jobId}", status.DashboardUrl);
    }

    [Fact]
    public void AddHangfireJobStatusOperations_rejects_null_arguments()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());

        Assert.Throws<ArgumentNullException>(() =>
            new OperationCatalogBuilder().AddHangfireJobStatusOperations(null!, storage, job));
        Assert.Throws<ArgumentNullException>(() =>
            new OperationCatalogBuilder().AddHangfireJobStatusOperations(client, null!, job));
        Assert.Throws<ArgumentNullException>(() =>
            new OperationCatalogBuilder().AddHangfireJobStatusOperations(client, storage, null!));
    }

    [Fact]
    public void AddHangfireJobStatusOperations_can_register_multiple_continuation_targets_under_distinct_names()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var reportJob = HangfireJobStatusOperations.ForJob<ParameterlessJob>();
        var cleanupJob = HangfireJobStatusOperations.ForJob<OptionsJob, ReportOptions>(new ReportOptions("weekly"));

        var catalog = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, reportJob, options =>
            {
                options.ContinuationOperationName = "continue-with-report-job";
            })
            .AddHangfireJobStatusOperations(client, storage, cleanupJob, options =>
            {
                options.ContinuationOperationName = "continue-with-cleanup-job";
                options.StatusOperationName = null;
            })
            .Build();

        Assert.Single(catalog.Operations, operation => operation.Name == "continue-with-report-job");
        Assert.Single(catalog.Operations, operation => operation.Name == "continue-with-cleanup-job");
        Assert.Single(catalog.Operations, operation => operation.Name == "get-hangfire-job-status");
    }

    [Fact]
    public void AddHangfireJobStatusOperations_registering_the_default_continuation_name_twice_throws_on_build()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());

        var builder = new OperationCatalogBuilder()
            .AddHangfireJobStatusOperations(client, storage, job)
            .AddHangfireJobStatusOperations(client, storage, job, options => options.StatusOperationName = null);

        Assert.Throws<OperationCatalogException>(() => builder.Build());
    }

    [Fact]
    public void AddHangfireJobStatusOperations_rejects_a_blank_continuation_operation_name()
    {
        using var storage = new InMemoryStorage();
        var client = new BackgroundJobClient(storage);
        var job = Job.FromExpression(() => TestJobs.CleanUp());

        Assert.Throws<ArgumentException>(() =>
            new OperationCatalogBuilder().AddHangfireJobStatusOperations(client, storage, job, options => options.ContinuationOperationName = "   "));
    }

    [Fact]
    public void ForJob_builds_a_job_for_a_parameterless_execution_method()
    {
        var job = HangfireJobStatusOperations.ForJob<ParameterlessJob>();

        Assert.Equal(typeof(ParameterlessJob), job.Type);
        Assert.Equal(nameof(ParameterlessJob.ExecuteAsync), job.Method.Name);
        Assert.Equal([CancellationToken.None], job.Args);
    }

    [Fact]
    public void ForJob_builds_a_job_for_an_options_based_execution_method()
    {
        var options = new ReportOptions("weekly");

        var job = HangfireJobStatusOperations.ForJob<OptionsJob, ReportOptions>(options);

        Assert.Equal(typeof(OptionsJob), job.Type);
        Assert.Equal(nameof(OptionsJob.ExecuteAsync), job.Method.Name);
        Assert.Equal([options, CancellationToken.None], job.Args);
    }

    [Fact]
    public void ForJob_throws_when_no_matching_execution_method_exists()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HangfireJobStatusOperations.ForJob<NoExecuteMethodJob>());
        Assert.Contains(nameof(NoExecuteMethodJob), exception.Message);
    }

    [Fact]
    public void ForJob_throws_when_multiple_matching_execution_methods_exist()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HangfireJobStatusOperations.ForJob<AmbiguousJob>());
        Assert.Contains(nameof(AmbiguousJob), exception.Message);
    }

    private static Task<OperationInvocationResult> InvokeAsync(OperationCatalog catalog, string operationName, string? parentJobId = null)
    {
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == operationName);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? inputs = parentJobId is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>
            {
                [operationName == "continue-hangfire-job" ? "parentJobId" : "jobId"] =
                    System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(parentJobId)).RootElement.Clone()
            };
        return CreateInvoker().InvokeAsync(operation, inputs).AsTask();
    }

    /// <summary>
    /// <c>continue-hangfire-job</c> defaults to <see cref="AgentSafetyLevel.Confirm"/>, which is metadata
    /// only. A confirming policy is supplied here so <see cref="OperationInvoker"/> actually executes it.
    /// </summary>
    private static OperationInvoker CreateInvoker() =>
        new(new NullServiceProvider(), policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]);

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

    private sealed class ParameterlessJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record ReportOptions(string Name);

    private sealed class OptionsJob
    {
        public Task ExecuteAsync(ReportOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoExecuteMethodJob
    {
        public void SomethingElse()
        {
        }
    }

    private sealed class AmbiguousJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Execute(CancellationToken cancellationToken)
        {
        }
    }
}
