using System.Reflection;
using System.Text.Json;
using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class HangfireJobRegistrationCatalogBuilderExtensionsTests
{
    /// <summary>
    /// Registered Hangfire jobs default to <see cref="AgentSafetyLevel.Confirm"/>, which is
    /// metadata only. Tests must supply a confirming policy for <see cref="OperationInvoker"/>
    /// to actually execute them.
    /// </summary>
    private static OperationInvoker CreateInvoker(IServiceProvider serviceProvider) =>
        new(serviceProvider, policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]);

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

        var result = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

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

        var result = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

        Assert.True(result.Succeeded);
        var created = Assert.Single(client.CreatedJobs);
        var options = Assert.IsType<CleanupOptions>(created.Job.Args[0]);
        Assert.Equal(25, options.BatchSize);
        Assert.Equal(CancellationToken.None, created.Job.Args[1]);
    }

    [Fact]
    public async Task RegisterJobs_discovers_a_brownfield_crtp_base_class_with_constructor_parameters()
    {
        // Mirrors issue #28: a pre-existing CRTP job hierarchy (not HangfireJob) whose constructor takes a
        // Hangfire PerformContext-like dependency. RegisterJobs must discover and enqueue it without ever
        // constructing an instance (discovery is reflection-only; Hangfire's JobActivator builds it at
        // execution time).
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<BrownfieldJobBase<BrownfieldReconciliationJob>>(
                client,
                [typeof(BrownfieldReconciliationJob).Assembly],
                options => options.Exclude = type => type != typeof(BrownfieldReconciliationJob))
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "brownfield-reconciliation-job");

        var result = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        Assert.True(result.Succeeded);
        var created = Assert.Single(client.CreatedJobs);
        Assert.Equal(typeof(BrownfieldReconciliationJob), created.Job.Type);
        Assert.Equal(nameof(IHangfireJob.ExecuteAsync), created.Job.Method.Name);
        Assert.Single(created.Job.Args);
        Assert.Equal(CancellationToken.None, created.Job.Args[0]);
        Assert.IsType<EnqueuedState>(created.State);
    }

    [Fact]
    public async Task RegisterJobs_with_options_discovers_a_brownfield_crtp_base_class_with_constructor_parameters()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterJobs<BrownfieldOptionsJobBase<BrownfieldOptionsJob>, BrownfieldOptions>(
                client,
                [typeof(BrownfieldOptionsJob).Assembly],
                options => options.Exclude = type => type != typeof(BrownfieldOptionsJob))
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "brownfield-options-job");
        var inputs = new Dictionary<string, JsonElement>
        {
            ["options"] = JsonDocument.Parse("{\"batchSize\":7}").RootElement.Clone()
        };

        var result = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

        Assert.True(result.Succeeded);
        var created = Assert.Single(client.CreatedJobs);
        Assert.Equal(typeof(BrownfieldOptionsJob), created.Job.Type);
        var boundOptions = Assert.IsType<BrownfieldOptions>(created.Job.Args[0]);
        Assert.Equal(7, boundOptions.BatchSize);
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
        await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

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

        var result = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

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
                options.Enrich = (_, metadata) =>
                {
                    metadata.Name = "enriched-cleanup";
                    metadata.Category = "Maintenance";
                    metadata.SafetyLevel = AgentSafetyLevel.Safe;
                    metadata.Aliases.Add("clean-now");
                };
            })
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "enriched-cleanup");
        Assert.Equal("Maintenance", operation.Category);
        Assert.Equal(AgentSafetyLevel.Dangerous, operation.SafetyLevel);
        Assert.Contains("clean-now", operation.Aliases);
    }

    [Fact]
    public void RegisterJobs_reports_registered_and_excluded_job_types()
    {
        HangfireJobRegistrationOptions? observed = null;

        new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(
                new RecordingBackgroundJobClient(),
                [typeof(CleanupJob).Assembly],
                options =>
                {
                    observed = options;
                    options.Exclude = type => type == typeof(ExcludedJob);
                })
            .Build();

        Assert.Contains(observed!.DiscoveryReports, report =>
            report.JobType == typeof(CleanupJob) &&
            report.Disposition == HangfireJobDiscoveryDisposition.Registered &&
            report.Method == nameof(HangfireJob.ExecuteAsync) &&
            report.OperationName == "cleanup-job");
        Assert.Contains(observed.DiscoveryReports, report =>
            report.JobType == typeof(ExcludedJob) &&
            report.Disposition == HangfireJobDiscoveryDisposition.Skipped &&
            report.Reason == "The job type was excluded by the configured predicate.");
    }

    [Fact]
    public void RegisterJobs_rejects_null_assemblies()
    {
        var assemblies = new Assembly?[] { typeof(CleanupJob).Assembly, null };

        var exception = Assert.Throws<ArgumentException>(() => new OperationCatalogBuilder()
            .RegisterJobs<DiscoveredJobBase>(new RecordingBackgroundJobClient(), assemblies!));

        Assert.Equal("assemblies", exception.ParamName);
    }

    [Fact]
    public async Task RegisterAllOptionsJobs_discovers_every_closed_options_type_in_one_call()
    {
        var client = new RecordingBackgroundJobClient();
        var catalog = new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(client, [typeof(ScanA).Assembly], options => options.Exclude = type =>
                type != typeof(ScanA) && type != typeof(ScanB))
            .Build();

        var operationA = Assert.Single(catalog.Operations, operation => operation.Name == "scan-a");
        var operationB = Assert.Single(catalog.Operations, operation => operation.Name == "scan-b");

        var inputsA = new Dictionary<string, JsonElement> { ["options"] = JsonDocument.Parse("{\"batchSize\":3}").RootElement.Clone() };
        var resultA = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operationA, inputsA);
        Assert.True(resultA.Succeeded);
        var createdA = Assert.Single(client.CreatedJobs);
        Assert.Equal(typeof(ScanA), createdA.Job.Type);
        Assert.Equal(3, Assert.IsType<ScanOptionsA>(createdA.Job.Args[0]).BatchSize);

        var inputsB = new Dictionary<string, JsonElement> { ["options"] = JsonDocument.Parse("{\"label\":\"x\"}").RootElement.Clone() };
        var resultB = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operationB, inputsB);
        Assert.True(resultB.Succeeded);
        var createdB = client.CreatedJobs.Single(created => created.Job.Type == typeof(ScanB));
        Assert.Equal("x", Assert.IsType<ScanOptionsB>(createdB.Job.Args[0]).Label);
    }

    [Fact]
    public void RegisterAllOptionsJobs_reports_a_job_with_multiple_options_interfaces_as_ambiguous()
    {
        HangfireJobRegistrationOptions? observed = null;

        var catalog = new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(
                new RecordingBackgroundJobClient(),
                [typeof(ScanMultiOptions).Assembly],
                options =>
                {
                    observed = options;
                    options.Exclude = type => type != typeof(ScanMultiOptions);
                })
            .Build();

        Assert.DoesNotContain(catalog.Operations, operation => operation.Name == "scan-multi-options");
        Assert.Contains(observed!.DiscoveryReports, report =>
            report.JobType == typeof(ScanMultiOptions) &&
            report.Disposition == HangfireJobDiscoveryDisposition.Skipped &&
            report.Reason.StartsWith("Multiple closed IHangfireJob<TOptions> interfaces were found;", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterAllOptionsJobs_rejects_ambiguous_options_interfaces_in_strict_mode()
    {
        Assert.Throws<OperationCatalogException>(() => new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(
                new RecordingBackgroundJobClient(),
                [typeof(ScanMultiOptions).Assembly],
                options =>
                {
                    options.Exclude = type => type != typeof(ScanMultiOptions);
                    options.StrictValidation = true;
                }));
    }

    [Fact]
    public void RegisterAllOptionsJobs_rejects_null_assemblies()
    {
        var assemblies = new Assembly?[] { typeof(ScanA).Assembly, null };

        var exception = Assert.Throws<ArgumentException>(() => new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(new RecordingBackgroundJobClient(), assemblies!));

        Assert.Equal("assemblies", exception.ParamName);
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

    // --- Brownfield fixtures (issue #28) -----------------------------------------------------
    //
    // These mirror a real pre-existing Hangfire job hierarchy (OPG Platform's IOpgJob/OpgJobBase<TSelf>):
    // a CRTP-style generic base class that is *not* HangfireJob, takes constructor parameters
    // (simulating a Hangfire PerformContext plus an injected dependency), and only implements
    // IHangfireJob to opt into RegisterJobs — no rewrite of the base class's inheritance chain.

    /// <summary>Simulates a Hangfire <c>PerformContext</c>-like dependency resolved by <c>JobActivator</c>.</summary>
    private sealed class FakePerformContext
    {
        public FakePerformContext(string jobId) => JobId = jobId;
        public string JobId { get; }
    }

    private interface IBrownfieldService
    {
        Task RunAsync(CancellationToken cancellationToken);
    }

    /// <summary>Pre-existing brownfield job interface, unrelated to this package.</summary>
    private interface IBrownfieldJob
    {
        Task RunAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// A CRTP-style brownfield base class with a constructor parameter, adopting <see cref="IHangfireJob"/>
    /// without deriving from <see cref="HangfireJob"/>. <see cref="RegisterJobs{TJobBase}"/> never constructs
    /// this type; only Hangfire's <see cref="JobActivator"/> does, at execution time.
    /// </summary>
    private abstract class BrownfieldJobBase<TSelf> : IBrownfieldJob, IHangfireJob
        where TSelf : BrownfieldJobBase<TSelf>
    {
        protected BrownfieldJobBase(FakePerformContext context) => Context = context;

        protected FakePerformContext Context { get; }

        public abstract Task RunAsync(CancellationToken cancellationToken);

        // Conventional IHangfireJob shape, implemented implicitly (public) so reflection discovery finds it.
        public Task ExecuteAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);
    }

    private sealed class BrownfieldReconciliationJob : BrownfieldJobBase<BrownfieldReconciliationJob>
    {
        private readonly IBrownfieldService _service;

        public BrownfieldReconciliationJob(FakePerformContext context, IBrownfieldService service)
            : base(context)
        {
            _service = service;
        }

        public override Task RunAsync(CancellationToken cancellationToken) => _service.RunAsync(cancellationToken);
    }

    private sealed class BrownfieldService : IBrownfieldService
    {
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BrownfieldOptions { public int BatchSize { get; set; } }

    private sealed class ScanOptionsA { public int BatchSize { get; set; } }
    private sealed class ScanOptionsB { public string? Label { get; set; } }
    private sealed class ScanA : HangfireJobWithOptions<ScanOptionsA>
    {
        public override Task ExecuteAsync(ScanOptionsA options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScanB : HangfireJobWithOptions<ScanOptionsB>
    {
        public override Task ExecuteAsync(ScanOptionsB options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScanMultiOptions : IHangfireJob<ScanOptionsA>, IHangfireJob<ScanOptionsB>
    {
        public Task ExecuteAsync(ScanOptionsA options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecuteAsync(ScanOptionsB options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A brownfield options-bearing base class taking a constructor parameter.</summary>
    private abstract class BrownfieldOptionsJobBase<TSelf> : IHangfireJob<BrownfieldOptions>
        where TSelf : BrownfieldOptionsJobBase<TSelf>
    {
        protected BrownfieldOptionsJobBase(FakePerformContext context) => Context = context;

        protected FakePerformContext Context { get; }

        public abstract Task ExecuteAsync(BrownfieldOptions options, CancellationToken cancellationToken);
    }

    private sealed class BrownfieldOptionsJob : BrownfieldOptionsJobBase<BrownfieldOptionsJob>
    {
        public BrownfieldOptionsJob(FakePerformContext context)
            : base(context)
        {
        }

        public override Task ExecuteAsync(BrownfieldOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
