using System.Text.Json;
using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class HangfireJobRegistrationCatalogBuilderExtensionsTests
{
    [Fact]
    public async Task RegisterJobs_discovers_concrete_jobs_and_enqueues_without_executing_them()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(client, [typeof(CleanupJob).Assembly])
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "cleanup-job");
        Assert.Equal("Hangfire jobs", operation.Category);
        Assert.Equal(AgentSafetyLevel.Confirm, operation.SafetyLevel);
        Assert.Empty(client.CreatedJobs);

        var result = await new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation);

        Assert.True(result.Succeeded);
        var created = Assert.Single(client.CreatedJobs);
        Assert.Equal(typeof(CleanupJob), created.Job.Type);
        Assert.Equal(nameof(HangfireJob.ExecuteAsync), created.Job.Method.Name);
        Assert.Single(created.Job.Args);
        Assert.Equal(CancellationToken.None, created.Job.Args[0]);
        Assert.IsType<EnqueuedState>(created.State);
    }

    [Fact]
    public async Task RegisterJobs_with_options_binds_json_and_passes_options_to_the_queued_job()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<OptionsJobBase, CleanupOptions>(client, [typeof(OptionsCleanupJob).Assembly])
            .Build();
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "options-cleanup-job");
        var inputs = new Dictionary<string, JsonElement>
        {
            ["options"] = JsonDocument.Parse("{\"batchSize\":25}").RootElement.Clone()
        };

        var result = await new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

        Assert.True(result.Succeeded);
        var created = Assert.Single(client.CreatedJobs);
        var options = Assert.IsType<CleanupOptions>(created.Job.Args[0]);
        Assert.Equal(25, options.BatchSize);
        Assert.Equal(CancellationToken.None, created.Job.Args[1]);
    }

    [Fact]
    public void RegisterJobs_discovers_inherited_and_closed_generic_jobs_exactly_once()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(client, [typeof(CleanupJob).Assembly], options => options.Exclude = type =>
                type != typeof(InheritedJob) && type != typeof(ClosedGenericJob))
            .Build();

        Assert.Single(catalog.Operations, operation => operation.Name == "inherited-job");
        Assert.Single(catalog.Operations, operation => operation.Name == "closed-generic-job");
    }

    [Fact]
    public async Task RegisterJobs_honors_a_custom_method_selector_for_overloads()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<OverloadedJobBase>(client, [typeof(OverloadedJob).Assembly], options =>
            {
                options.Exclude = type => type != typeof(OverloadedJob);
                options.MethodSelector = type => type.GetMethod(nameof(OverloadedJob.ExecuteAsync), [typeof(CancellationToken)]);
            })
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "overloaded-job");
        await new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation);

        var created = Assert.Single(client.CreatedJobs);
        Assert.Single(created.Job.Method.GetParameters());
    }

    [Fact]
    public void RegisterJobs_reports_and_rejects_an_invalid_custom_method_selector()
    {
        var permissiveClient = new RecordingBackgroundJobClient();
        HangfireJobRegistrationOptions? observed = null;
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(permissiveClient, [typeof(CleanupJob).Assembly], options =>
            {
                observed = options;
                options.Exclude = type => type != typeof(CleanupJob);
                options.MethodSelector = _ => typeof(CleanupJob).GetMethod(nameof(ToString));
            })
            .Build();

        Assert.Empty(catalog.Operations);
        Assert.Contains(observed!.Diagnostics, diagnostic => diagnostic.JobType == typeof(CleanupJob));

        Assert.Throws<OperationCatalogException>(() => new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(new RecordingBackgroundJobClient(), [typeof(CleanupJob).Assembly], options =>
            {
                options.Exclude = type => type != typeof(CleanupJob);
                options.MethodSelector = _ => typeof(CleanupJob).GetMethod(nameof(ToString));
                options.StrictValidation = true;
            }));
    }

    [Fact]
    public async Task RegisterJobs_with_options_fails_binding_and_does_not_enqueue_on_malformed_json()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<OptionsJobBase, CleanupOptions>(client, [typeof(OptionsCleanupJob).Assembly])
            .Build();
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "options-cleanup-job");

        var inputs = new Dictionary<string, JsonElement>
        {
            ["options"] = JsonDocument.Parse("{\"batchSize\":\"not-a-number\"}").RootElement.Clone()
        };

        var result = await new OperationInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

        Assert.False(result.Succeeded);
        Assert.Empty(client.CreatedJobs);
    }

    [Fact]
    public void RegisterJobs_orders_discovered_operations_deterministically()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<OrderedJobBase>(client, [typeof(ZebraJob).Assembly])
            .Build();

        var names = catalog.Operations
            .Where(operation => operation.Name is "alpha-job" or "mid-job" or "zebra-job")
            .Select(operation => operation.Name)
            .ToArray();

        Assert.Equal(["alpha-job", "mid-job", "zebra-job"], names);
    }

    [Fact]
    public void RegisterJobs_excludes_abstract_and_open_generic_types_and_honors_exclusions()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(client, [typeof(CleanupJob).Assembly], options => options.Exclude = type => type == typeof(ExcludedJob))
            .Build();

        Assert.DoesNotContain(catalog.Operations, operation => operation.Name == "abstract-job");
        Assert.DoesNotContain(catalog.Operations, operation => operation.Name == "generic-job");
        Assert.DoesNotContain(catalog.Operations, operation => operation.Name == "excluded-job");
        Assert.Contains(catalog.Operations, operation => operation.Name == "cleanup-job");
    }

    [Fact]
    public void RegisterJobs_reports_ambiguity_permissively_and_rejects_it_in_strict_mode()
    {
        var permissiveClient = new RecordingBackgroundJobClient();
        HangfireJobRegistrationOptions? observed = null;
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<AmbiguousJobBase>(permissiveClient, [typeof(AmbiguousJob).Assembly], options => observed = options)
            .Build();

        Assert.Single(catalog.Operations);
        Assert.Contains(observed!.Diagnostics, diagnostic => diagnostic.JobType == typeof(AmbiguousJob));

        var strictClient = new RecordingBackgroundJobClient();
        Assert.Throws<OperationCatalogException>(() => new OperationCatalogBuilder()
            .RegisterJobs<AmbiguousJobBase>(strictClient, [typeof(AmbiguousJob).Assembly], options => options.StrictValidation = true));
    }

    [Fact]
    public void RegisterJobs_allows_metadata_enrichment_but_does_not_downgrade_dangerous_defaults()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(client, [typeof(CleanupJob).Assembly], options =>
            {
                options.Exclude = type => type != typeof(CleanupJob);
                options.SafetyLevel = AgentSafetyLevel.Dangerous;
                options.EnrichAsync = (_, metadata) =>
                {
                    metadata.Name = "enriched-cleanup";
                    metadata.Category = "Maintenance";
                    metadata.SafetyLevel = AgentSafetyLevel.Safe;
                    metadata.Aliases.Add("clean-now");
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "enriched-cleanup");
        Assert.Equal("Maintenance", operation.Category);
        Assert.Equal(AgentSafetyLevel.Dangerous, operation.SafetyLevel);
        Assert.Contains("clean-now", operation.Aliases);
    }

    private sealed class RecordingBackgroundJobClient : IBackgroundJobClient
    {
        public List<(Job Job, IState State)> CreatedJobs { get; } = [];
        public string Create(Job job, IState state) { CreatedJobs.Add((job, state)); return "job-id"; }
        public bool ChangeState(string jobId, IState state, string? expectedState) => true;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private abstract class DiscoveredJobBase : HangfireJob { }
    private sealed class CleanupJob : DiscoveredJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class ExcludedJob : DiscoveredJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private abstract class AbstractJob : DiscoveredJobBase { }
    private sealed class GenericJob<T> : DiscoveredJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private class InheritedJobBase : DiscoveredJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class InheritedJob : InheritedJobBase { }
    private class GenericJobBase<T> : DiscoveredJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class ClosedGenericJob : GenericJobBase<int> { }

    private abstract class OverloadedJobBase : HangfireJob { }
    private sealed class OverloadedJob : OverloadedJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecuteAsync(int count, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private abstract class OrderedJobBase : HangfireJob { }
    private sealed class ZebraJob : OrderedJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class AlphaJob : OrderedJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class MidJob : OrderedJobBase { public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask; }

    private sealed class CleanupOptions { public int BatchSize { get; set; } }
    private abstract class OptionsJobBase : HangfireJobWithOptions<CleanupOptions> { }
    private sealed class OptionsCleanupJob : OptionsJobBase { public override Task ExecuteAsync(CleanupOptions options, CancellationToken cancellationToken) => Task.CompletedTask; }

    private abstract class AmbiguousJobBase : HangfireJob { }
    private sealed class AmbiguousJob : AmbiguousJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
