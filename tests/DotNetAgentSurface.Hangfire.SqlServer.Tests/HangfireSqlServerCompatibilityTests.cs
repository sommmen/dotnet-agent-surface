using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Hangfire.Storage;

namespace DotNetAgentSurface.Hangfire.SqlServer.Tests;

/// <summary>
/// Opt-in compatibility coverage for <c>AddHangfireRecurringOperations(...)</c> against a real,
/// supported Hangfire SQL Server storage provider (issue #22). These tests only run when
/// <see cref="SqlServerCompatibilityFixture.OptInEnvironmentVariable"/> is set and a throwaway
/// SQL Server container could be started; otherwise every test in this class reports "skipped"
/// rather than failing, so the suite never requires Docker or credentials by default.
/// </summary>
[Collection(SqlServerCompatibilityCollection.Name)]
public sealed class HangfireSqlServerCompatibilityTests
{
    private readonly SqlServerCompatibilityFixture _fixture;

    public HangfireSqlServerCompatibilityTests(SqlServerCompatibilityFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void AddHangfireRecurringOperations_does_not_access_sql_server_storage_until_invocation()
    {
        SkipUnlessAvailable();

        // An unreachable SQL Server (closed port, minimal timeout) proves catalog construction
        // never opens a connection: PrepareSchemaIfNecessary is disabled so the SqlServerStorage
        // constructor itself does not attempt to connect, and AddHangfireRecurringOperations must
        // not either.
        var unreachableStorage = new SqlServerStorage(
            "Server=127.0.0.1,1;Database=NoSuchDatabase;Connect Timeout=1;TrustServerCertificate=True;Encrypt=False;Integrated Security=True;",
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
        var manager = new RecurringJobManager(unreachableStorage);

        var catalog = new OperationCatalogBuilder()
            .AddHangfireRecurringOperations(unreachableStorage, manager)
            .Build();

        var list = Assert.Single(catalog.Operations, operation => operation.Name == "list-recurring-hangfire");
        Assert.Equal("Hangfire", list.Category);
        var trigger = Assert.Single(catalog.Operations, operation => operation.Name == "trigger-recurring-hangfire");
        Assert.Equal(AgentSafetyLevel.Confirm, trigger.SafetyLevel);
    }

    [SkippableFact]
    public async Task List_recurring_hangfire_reflects_jobs_registered_against_sql_server_storage()
    {
        SkipUnlessAvailable();

        var storage = _fixture.CreateStorage();
        var manager = new RecurringJobManager(storage);
        var prefix = $"compat-list-{Guid.NewGuid():N}-";
        manager.AddOrUpdate(prefix + "zebra", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());
        manager.AddOrUpdate(prefix + "alpha", Job.FromExpression(() => TestJobs.CleanUp()), Cron.Hourly());

        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(storage, manager).Build();

        var result = await InvokeAsync(catalog, "list-recurring-hangfire");

        Assert.True(result.Succeeded);
        var jobs = Assert.IsAssignableFrom<IReadOnlyList<RecurringHangfireJobInfo>>(result.Value);
        var ownJobs = jobs.Where(job => job.JobId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal([prefix + "alpha", prefix + "zebra"], ownJobs.Select(job => job.JobId));
        Assert.All(ownJobs, job => Assert.Equal(typeof(TestJobs).FullName, job.JobType));
    }

    [SkippableFact]
    public async Task Trigger_recurring_hangfire_rejects_unknown_job_without_triggering_manager()
    {
        SkipUnlessAvailable();

        var storage = _fixture.CreateStorage();
        var manager = new RecurringJobManager(storage);
        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(storage, manager).Build();

        var missingJobId = $"compat-missing-{Guid.NewGuid():N}";
        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", missingJobId);

        Assert.True(result.Succeeded);
        var acknowledgement = Assert.IsType<TriggerRecurringHangfireResult>(result.Value);
        Assert.Equal("rejected", acknowledgement.Status);
        Assert.Equal(missingJobId, acknowledgement.JobId);
    }

    [SkippableFact]
    public async Task Trigger_recurring_hangfire_enqueues_a_job_registered_in_sql_server_storage()
    {
        SkipUnlessAvailable();

        var storage = _fixture.CreateStorage();
        var manager = new RecurringJobManager(storage);
        var jobId = $"compat-trigger-{Guid.NewGuid():N}";
        manager.AddOrUpdate(jobId, Job.FromExpression(() => TestJobs.CleanUp()), Cron.Daily());
        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(storage, manager).Build();

        var result = await InvokeAsync(catalog, "trigger-recurring-hangfire", jobId);

        Assert.True(result.Succeeded);
        var acknowledgement = Assert.IsType<TriggerRecurringHangfireResult>(result.Value);
        Assert.Equal("enqueued", acknowledgement.Status);
        Assert.Equal(jobId, acknowledgement.JobId);
        Assert.NotNull(acknowledgement.EnqueueId);

        using var connection = storage.GetConnection();
        var registeredJob = Assert.Single(connection.GetRecurringJobs(), job => job.Id == jobId);
        Assert.Equal(acknowledgement.EnqueueId, registeredJob.LastJobId);
    }

    [SkippableFact]
    public async Task List_recurring_hangfire_translates_sql_server_storage_failures_into_operation_failures()
    {
        SkipUnlessAvailable();

        // A connection string that resolves but is refused quickly (closed port, minimal timeout)
        // exercises the same failure-translation path a genuine outage or credential problem
        // would hit: the ADO.NET/Hangfire exception must surface as
        // OperationInvocationResult.Failure(...), never as an unhandled exception.
        var unreachableStorage = new SqlServerStorage(
            "Server=127.0.0.1,1;Database=NoSuchDatabase;Connect Timeout=1;TrustServerCertificate=True;Encrypt=False;Integrated Security=True;",
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
        var manager = new RecurringJobManager(unreachableStorage);
        var catalog = new OperationCatalogBuilder().AddHangfireRecurringOperations(unreachableStorage, manager).Build();

        var result = await InvokeAsync(catalog, "list-recurring-hangfire");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    private void SkipUnlessAvailable() => Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);

    private static Task<OperationInvocationResult> InvokeAsync(OperationCatalog catalog, string operationName, string? jobId = null)
    {
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == operationName);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? inputs = jobId is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["jobId"] = System.Text.Json.JsonSerializer.SerializeToElement(jobId)
            };
        return new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs).AsTask();
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
