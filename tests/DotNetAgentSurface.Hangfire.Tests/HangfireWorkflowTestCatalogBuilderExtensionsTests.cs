using System.Reflection;
using System.Text.Json;
using DotNetAgentSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Hangfire.Tests;

public sealed class HangfireWorkflowTestCatalogBuilderExtensionsTests
{
    /// <summary>
    /// Workflow test operations default to <see cref="AgentSafetyLevel.Confirm"/>, which is metadata only.
    /// Tests must supply a confirming policy for <see cref="OperationInvoker"/> to actually execute them.
    /// </summary>
    private static OperationInvoker CreateInvoker(IServiceProvider serviceProvider) =>
        new(serviceProvider, policies: [new DangerousOperationConfirmationPolicy((_, _, _) => ValueTask.FromResult(true))]);

    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public async Task RegisterWorkflowTests_runs_a_parameterless_job_directly_and_captures_its_transcript()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(LoggingJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(LoggingJob))
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "logging-job");
        Assert.Equal("Hangfire workflow tests", operation.Category);
        Assert.Equal(AgentSafetyLevel.Confirm, operation.SafetyLevel);

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        Assert.True(invocation.Succeeded);
        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.True(result.Status == HangfireWorkflowTestStatus.Succeeded, result.Error);
        Assert.Contains("hello from job", result.Transcript);
        Assert.Null(result.Error);
        Assert.Null(result.ArtifactPath);
    }

    [Fact]
    public async Task RegisterWorkflowTests_with_options_binds_json_input_and_passes_it_to_the_job()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<OptionsWorkflowJobBase, WorkflowOptions>(EmptyServices(), [typeof(OptionsLoggingJob).Assembly])
            .Build();
        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "options-logging-job");
        var inputs = new Dictionary<string, JsonElement>
        {
            ["workflowOptions"] = JsonDocument.Parse("{\"message\":\"agent says hi\"}").RootElement.Clone()
        };

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

        Assert.True(invocation.Succeeded);
        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Succeeded, result.Status);
        Assert.Contains("agent says hi", result.Transcript);
    }

    [Fact]
    public async Task RegisterWorkflowTests_activates_the_job_through_di_when_registered()
    {
        var services = new ServiceCollection().AddSingleton(new Dependency("injected-value")).BuildServiceProvider();
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(services, [typeof(DependentJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(DependentJob))
            .Build();
        var operation = Assert.Single(catalog.Operations);

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Succeeded, result.Status);
        Assert.Contains("injected-value", result.Transcript);
    }

    [Fact]
    public async Task RegisterWorkflowTests_reports_failed_status_and_error_when_the_job_throws()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(FailingJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(FailingJob))
            .Build();
        var operation = Assert.Single(catalog.Operations);

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        Assert.True(invocation.Succeeded);
        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task RegisterWorkflowTests_reports_timed_out_status_when_the_job_exceeds_its_timeout()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(SlowJob).Assembly], configure: options =>
            {
                options.Exclude = type => type != typeof(SlowJob);
                options.Timeout = TimeSpan.FromMilliseconds(200);
            })
            .Build();
        var operation = Assert.Single(catalog.Operations);

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.TimedOut, result.Status);
        Assert.Contains("timeout", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterWorkflowTests_reports_cancelled_status_when_the_caller_cancels()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(SlowJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(SlowJob))
            .Build();
        var operation = Assert.Single(catalog.Operations);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation, cancellationToken: cts.Token);

        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task RegisterWorkflowTests_writes_a_transcript_artifact_when_an_artifact_directory_is_configured()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-surface-workflow-tests-{Guid.NewGuid():N}");
        try
        {
            var catalog = new OperationCatalogBuilder()
                .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(LoggingJob).Assembly], configure: options =>
                {
                    options.Exclude = type => type != typeof(LoggingJob);
                    options.ArtifactDirectory = directory;
                })
                .Build();
            var operation = Assert.Single(catalog.Operations);

            var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

            var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
            Assert.NotNull(result.ArtifactPath);
            Assert.True(File.Exists(result.ArtifactPath));
            var contents = await File.ReadAllTextAsync(result.ArtifactPath);
            Assert.Equal(result.Transcript, contents);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RegisterWorkflowTests_rejects_a_custom_method_selector_that_returns_a_non_public_method()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(NonPublicMethodJob).Assembly], configure: options =>
            {
                options.Exclude = type => type != typeof(NonPublicMethodJob);
                options.MethodSelector = type => type.GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            }));

        Assert.Contains("No supported public ExecuteAsync or Execute method", exception.Message);
    }

    [Fact]
    public void RegisterWorkflowTests_rejects_a_custom_method_selector_that_returns_a_static_method()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(StaticMethodJob).Assembly], configure: options =>
            {
                options.Exclude = type => type != typeof(StaticMethodJob);
                options.MethodSelector = type => type.GetMethod("ExecuteStaticAsync", BindingFlags.Static | BindingFlags.Public);
            }));

        Assert.Contains("No supported public ExecuteAsync or Execute method", exception.Message);
    }

    [Fact]
    public void RegisterWorkflowTests_rejects_null_builder_services_or_assemblies()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HangfireWorkflowTestCatalogBuilderExtensions.RegisterWorkflowTests<WorkflowJobBase>(null!, EmptyServices(), [typeof(LoggingJob).Assembly]));
        Assert.Throws<ArgumentNullException>(() => new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(null!, [typeof(LoggingJob).Assembly]));
        Assert.Throws<ArgumentNullException>(() => new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), null!));
    }

    [Fact]
    public void RegisterWorkflowTests_rejects_null_assembly_in_assemblies_enumerable()
    {
        // Verify that null assemblies in the enumerable are caught and reported with a clear error message.
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var builder = new OperationCatalogBuilder();
            // Use a custom enumerable that yields a null assembly to trigger the validation.
            var assembliesWithNull = new[] { typeof(LoggingJob).Assembly, null! };
            builder.RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), assembliesWithNull);
        });
        
        Assert.Contains("null entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("assemblies", ex.ParamName);
    }

    [Fact]
    public async Task RegisterWorkflowTests_prevents_artifact_filename_collision_with_guid_suffix()
    {
        // Verify that two concurrent writes to artifacts have unique filenames despite same timestamp.
        var artifactDir = Path.Combine(Path.GetTempPath(), $"hangfire-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(artifactDir);
            
            var catalog = new OperationCatalogBuilder()
                .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(LoggingJob).Assembly],
                    configure: options =>
                    {
                        options.Exclude = type => type != typeof(LoggingJob);
                        options.ArtifactDirectory = artifactDir;
                    })
                .Build();
            var operation = Assert.Single(catalog.Operations);

            // Run the same job twice in rapid succession (same millisecond very likely).
            var invoker = CreateInvoker(new NullServiceProvider());
            var invocation1Task = invoker.InvokeAsync(operation).AsTask();
            var invocation2Task = invoker.InvokeAsync(operation).AsTask();

            await Task.WhenAll(invocation1Task, invocation2Task);

            var result1 = Assert.IsType<HangfireWorkflowTestResult>(invocation1Task.Result.Value);
            var result2 = Assert.IsType<HangfireWorkflowTestResult>(invocation2Task.Result.Value);

            // Both should have artifact paths and they should be different.
            Assert.NotNull(result1.ArtifactPath);
            Assert.NotNull(result2.ArtifactPath);
            Assert.NotEqual(result1.ArtifactPath, result2.ArtifactPath);
            
            // Both files should exist and contain the logging output.
            Assert.True(File.Exists(result1.ArtifactPath));
            Assert.True(File.Exists(result2.ArtifactPath));
            Assert.Contains("hello from job", File.ReadAllText(result1.ArtifactPath));
            Assert.Contains("hello from job", File.ReadAllText(result2.ArtifactPath));
        }
        finally
        {
            if (Directory.Exists(artifactDir))
            {
                Directory.Delete(artifactDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RegisterWorkflowTests_handles_concurrent_logging_from_multiple_threads_safely()
    {
        // Verify that concurrent logging does not corrupt the transcript due to unsynchronized StringBuilder access.
        var catalog = new OperationCatalogBuilder()
            .RegisterWorkflowTests<WorkflowJobBase>(EmptyServices(), [typeof(ConcurrentLoggingJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(ConcurrentLoggingJob))
            .Build();
        var operation = Assert.Single(catalog.Operations);

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.True(result.Status == HangfireWorkflowTestStatus.Succeeded, result.Error);
        
        // Verify all expected log lines are present and the transcript is well-formed (not corrupted).
        var transcript = result.Transcript;
        Assert.NotEmpty(transcript);
        // Each line should contain a timestamp and thread ID marker; if StringBuilder wasn't thread-safe,
        // lines would be interleaved/corrupted and this assertion would fail.
        var lines = transcript.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 10, $"Expected at least 10 log lines from concurrent threads, got {lines.Length}");
        // Verify lines are properly formatted with timestamps and thread info
        foreach (var line in lines.Where(l => l.Contains("Thread")))
        {
            Assert.Contains("iteration", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RegisterAttributeWorkflowTests_runs_an_attribute_marked_job_implementing_only_a_foreign_interface()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterAttributeWorkflowTests(EmptyServices(), [typeof(AttributeMarkedForeignLoggingJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(AttributeMarkedForeignLoggingJob))
            .Build();

        var operation = Assert.Single(catalog.Operations, operation => operation.Name == "attribute-marked-foreign-logging-job");

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation);

        Assert.True(invocation.Succeeded);
        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Succeeded, result.Status);
        Assert.Contains("hello from attribute job", result.Transcript);
    }

    [Fact]
    public async Task RegisterAttributeWorkflowTests_runs_an_options_based_attribute_marked_job()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterAttributeWorkflowTests(EmptyServices(), [typeof(AttributeMarkedForeignOptionsLoggingJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(AttributeMarkedForeignOptionsLoggingJob))
            .Build();

        var operation = Assert.Single(catalog.Operations);
        var inputs = new Dictionary<string, JsonElement>
        {
            ["workflowOptions"] = JsonDocument.Parse("{\"message\":\"agent options hi\"}").RootElement.Clone()
        };

        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operation, inputs);

        Assert.True(invocation.Succeeded);
        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Succeeded, result.Status);
        Assert.Contains("agent options hi", result.Transcript);
    }

    [Fact]
    public void RegisterAttributeWorkflowTests_does_not_discover_an_unmarked_type_implementing_only_a_foreign_interface()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterAttributeWorkflowTests(EmptyServices(), [typeof(UnmarkedForeignLoggingJob).Assembly],
                configure: options => options.Exclude = type => type != typeof(UnmarkedForeignLoggingJob))
            .Build();

        Assert.Empty(catalog.Operations);
    }

    [Fact]
    public void RegisterAttributeWorkflowTests_skips_and_reports_a_type_with_ambiguously_shaped_execution_methods()
    {
        HangfireWorkflowTestRegistrationOptions? observed = null;
        var catalog = new OperationCatalogBuilder()
            .RegisterAttributeWorkflowTests(EmptyServices(), [typeof(AmbiguousShapedAttributeWorkflowJob).Assembly],
                configure: options =>
                {
                    observed = options;
                    options.Exclude = type => type != typeof(AmbiguousShapedAttributeWorkflowJob);
                })
            .Build();

        Assert.Empty(catalog.Operations);
        Assert.Contains(observed!.DiscoveryReports, report =>
            report.JobType == typeof(AmbiguousShapedAttributeWorkflowJob) &&
            report.Disposition == HangfireJobDiscoveryDisposition.Skipped &&
            report.Reason.Contains("differently-shaped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegisterAttributeWorkflowTests_rejects_ambiguously_shaped_types_in_strict_mode()
    {
        Assert.Throws<OperationCatalogException>(() => new OperationCatalogBuilder()
            .RegisterAttributeWorkflowTests(EmptyServices(), [typeof(AmbiguousShapedAttributeWorkflowJob).Assembly],
                configure: options =>
                {
                    options.Exclude = type => type != typeof(AmbiguousShapedAttributeWorkflowJob);
                    options.StrictValidation = true;
                }));
    }

    [Fact]
    public async Task RegisterAllOptionsJobs_with_services_discovers_every_closed_options_type_in_one_call()
    {
        var catalog = new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(EmptyServices(), [typeof(WorkflowScanA).Assembly],
                options => options.Exclude = type => type != typeof(WorkflowScanA) && type != typeof(WorkflowScanB))
            .Build();

        var operationA = Assert.Single(catalog.Operations, operation => operation.Name == "workflow-scan-a");
        Assert.Single(catalog.Operations, operation => operation.Name == "workflow-scan-b");

        var inputs = new Dictionary<string, JsonElement> { ["options"] = JsonDocument.Parse("{\"message\":\"hi\"}").RootElement.Clone() };
        var invocation = await CreateInvoker(new NullServiceProvider()).InvokeAsync(operationA, inputs);

        Assert.True(invocation.Succeeded);
        var result = Assert.IsType<HangfireWorkflowTestResult>(invocation.Value);
        Assert.Equal(HangfireWorkflowTestStatus.Succeeded, result.Status);
    }

    [Fact]
    public void RegisterAllOptionsJobs_with_services_skips_and_reports_a_job_with_no_valid_execution_method()
    {
        HangfireWorkflowTestRegistrationOptions? observed = null;
        var catalog = new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(EmptyServices(), [typeof(WorkflowNoValidMethodJob).Assembly],
                options =>
                {
                    observed = options;
                    options.Exclude = type => type != typeof(WorkflowNoValidMethodJob);
                })
            .Build();

        Assert.Empty(catalog.Operations);
        Assert.Contains(observed!.DiscoveryReports, report =>
            report.JobType == typeof(WorkflowNoValidMethodJob) &&
            report.Disposition == HangfireJobDiscoveryDisposition.Skipped &&
            report.Reason.Contains("No supported public ExecuteAsync or Execute method", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegisterAllOptionsJobs_with_services_rejects_a_job_with_no_valid_execution_method_in_strict_mode()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => new OperationCatalogBuilder()
            .RegisterAllOptionsJobs(EmptyServices(), [typeof(WorkflowNoValidMethodJob).Assembly],
                options =>
                {
                    options.Exclude = type => type != typeof(WorkflowNoValidMethodJob);
                    options.StrictValidation = true;
                }));

        Assert.Contains("No supported public ExecuteAsync or Execute method", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // --- Attribute-based fixtures -------------------------------------------------------------
    //
    // These simulate a pre-existing production job base class implementing its own unrelated marker
    // interface (see issue description: OPG Platform's IOpgJob<TOptions>/OpgJobBase<TOptions, TSelf>).
    // None of these types implement IHangfireJob/IHangfireJob<TOptions>.

    private interface IForeignWorkflowJob
    {
        Task ExecuteAsync(CancellationToken cancellationToken);
    }

    private interface IForeignOptionsWorkflowJob<TOptions>
    {
        Task ExecuteAsync(TOptions options, CancellationToken cancellationToken);
    }

    [HangfireJob]
    private sealed class AttributeMarkedForeignLoggingJob(ILogger<AttributeMarkedForeignLoggingJob> logger) : IForeignWorkflowJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("hello from attribute job");
            return Task.CompletedTask;
        }
    }

    [HangfireJob]
    private sealed class AttributeMarkedForeignOptionsLoggingJob(ILogger<AttributeMarkedForeignOptionsLoggingJob> logger) : IForeignOptionsWorkflowJob<WorkflowOptions>
    {
        public Task ExecuteAsync(WorkflowOptions options, CancellationToken cancellationToken)
        {
            logger.LogInformation("{Message}", options.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class UnmarkedForeignLoggingJob : IForeignWorkflowJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [HangfireJob]
    private sealed class AmbiguousShapedAttributeWorkflowJob : IForeignWorkflowJob, IForeignOptionsWorkflowJob<WorkflowOptions>
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExecuteAsync(WorkflowOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed record WorkflowOptions(string Message);
    public sealed record Dependency(string Value);

    // --- RegisterAllOptionsJobs(IServiceProvider, ...) fixtures --------------------------------
    //
    // These exercise the closed-generic IHangfireJob<TOptions> scanning path (not attribute-based), mirroring
    // HangfireJobRegistrationCatalogBuilderExtensionsTests' enqueue-side ScanA/ScanB/no-valid-method coverage,
    // to prove the workflow-test overload reports/rejects an invalid candidate instead of throwing unconditionally.

    private sealed class WorkflowScanA : IHangfireJob<WorkflowOptions>
    {
        public Task ExecuteAsync(WorkflowOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class WorkflowScanB : IHangfireJob<WorkflowScanBOptions>
    {
        public Task ExecuteAsync(WorkflowScanBOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed record WorkflowScanBOptions(string Label);

    // Implements IHangfireJob<TOptions> so it is discovered as a candidate, but its only ExecuteAsync method is an
    // explicit interface implementation (not public), so no valid execution method can be selected for it.
    internal sealed class WorkflowNoValidMethodJob : IHangfireJob<WorkflowOptions>
    {
        Task IHangfireJob<WorkflowOptions>.ExecuteAsync(WorkflowOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private abstract class WorkflowJobBase : HangfireJob { }
    private abstract class OptionsWorkflowJobBase : HangfireJobWithOptions<WorkflowOptions> { }

    private sealed class LoggingJob(ILogger<LoggingJob> logger) : WorkflowJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("hello from job");
            return Task.CompletedTask;
        }
    }

    private sealed class OptionsLoggingJob(ILogger<OptionsLoggingJob> logger) : OptionsWorkflowJobBase
    {
        public override Task ExecuteAsync(WorkflowOptions options, CancellationToken cancellationToken)
        {
            logger.LogInformation("{Message}", options.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class DependentJob(Dependency dependency, ILogger<DependentJob> logger) : WorkflowJobBase
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            logger.LogInformation("{Value}", dependency.Value);
        }
    }

    private sealed class FailingJob : WorkflowJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class SlowJob : WorkflowJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class NonPublicMethodJob : WorkflowJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // Deliberately shadows the base signature with a non-public member so the custom selector below finds it.
        private Task ExecuteAsync(int unused, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StaticMethodJob : WorkflowJobBase
    {
        public override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public static Task ExecuteStaticAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ConcurrentLoggingJob(ILogger<ConcurrentLoggingJob> logger) : WorkflowJobBase
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            // Spawn multiple tasks that log concurrently to stress-test thread safety of the TranscriptLogger.
            var tasks = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                int threadId = i;
                tasks.Add(Task.Run(() =>
                {
                    for (int j = 0; j < 3; j++)
                    {
                        logger.LogInformation($"Thread {threadId}, iteration {j}");
                    }
                }, cancellationToken));
            }
            await Task.WhenAll(tasks);
        }
    }
}
