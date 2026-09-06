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
                options.Timeout = TimeSpan.FromMilliseconds(50);
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
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

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

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public sealed record WorkflowOptions(string Message);
    public sealed record Dependency(string Value);

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
}
