using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DotNetAgentSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Adds operations that execute Hangfire jobs in-process and capture their logs for workflow testing.
/// </summary>
public static class HangfireWorkflowTestCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers concrete implementations of <typeparamref name="TJobBase"/> and registers a direct workflow-test operation for each one.
    /// </summary>
    public static OperationCatalogBuilder RegisterWorkflowTests<TJobBase>(
        this OperationCatalogBuilder builder,
        IServiceProvider services,
        IEnumerable<Assembly> assemblies,
        Action<HangfireWorkflowTestRegistrationOptions>? configure = null)
        where TJobBase : IHangfireJob
    {
        ThrowIfNull(builder);
        ThrowIfNull(services);
        ThrowIfNull(assemblies);

        var options = new HangfireWorkflowTestRegistrationOptions();
        configure?.Invoke(options);
        Validate(options);

        var jobBaseType = typeof(TJobBase);
        foreach (var jobType in GetLoadableTypes(assemblies)
                     .Where(type => IsConcreteClosedClass(type) && jobBaseType.IsAssignableFrom(type))
                     .Where(type => options.Exclude?.Invoke(type) != true)
                     .OrderBy(type => NormalizeName(options.NameFactory?.Invoke(type) ?? ToKebabCase(type.Name), options.Category), StringComparer.Ordinal))
        {
            var method = SelectMethod(jobType, jobBaseType, options);
            var name = options.NameFactory?.Invoke(jobType) ?? ToKebabCase(jobType.Name);
            var runner = new WorkflowTestRunner(services, jobType, method, options);
            builder.Add(name, $"Runs {jobType.Name} directly and captures its log transcript.", (Func<CancellationToken, Task<HangfireWorkflowTestResult>>)runner.RunAsync, registration =>
            {
                registration.Category = options.Category;
                registration.SafetyLevel = options.SafetyLevel;
            });
        }

        return builder;
    }

    /// <summary>
    /// Discovers concrete implementations of <typeparamref name="TJobBase"/> that accept <typeparamref name="TOptions"/> and registers a direct workflow-test operation for each one.
    /// </summary>
    public static OperationCatalogBuilder RegisterWorkflowTests<TJobBase, TOptions>(
        this OperationCatalogBuilder builder,
        IServiceProvider services,
        IEnumerable<Assembly> assemblies,
        Action<HangfireWorkflowTestRegistrationOptions>? configure = null)
        where TJobBase : IHangfireJob<TOptions>
    {
        ThrowIfNull(builder);
        ThrowIfNull(services);
        ThrowIfNull(assemblies);

        var options = new HangfireWorkflowTestRegistrationOptions();
        configure?.Invoke(options);
        Validate(options);

        var jobBaseType = typeof(TJobBase);
        foreach (var jobType in GetLoadableTypes(assemblies)
                     .Where(type => IsConcreteClosedClass(type) && jobBaseType.IsAssignableFrom(type))
                     .Where(type => options.Exclude?.Invoke(type) != true)
                     .OrderBy(type => NormalizeName(options.NameFactory?.Invoke(type) ?? ToKebabCase(type.Name), options.Category), StringComparer.Ordinal))
        {
            var method = SelectMethod(jobType, jobBaseType, options);
            var name = options.NameFactory?.Invoke(jobType) ?? ToKebabCase(jobType.Name);
            var runner = new WorkflowTestRunner(services, jobType, method, options);
            builder.Add(name, $"Runs {jobType.Name} directly and captures its log transcript.", (Func<TOptions, CancellationToken, Task<HangfireWorkflowTestResult>>)runner.RunAsync, registration =>
            {
                registration.Category = options.Category;
                registration.SafetyLevel = options.SafetyLevel;
            });
        }

        return builder;
    }

    /// <summary>
    /// Discovers every concrete job that implements exactly one closed <see cref="IHangfireJob{TOptions}"/> interface and
    /// registers a direct workflow-test operation for each job. The operations execute in-process and capture their
    /// <see cref="ILogger"/> transcript; they do not enqueue a Hangfire job.
    /// </summary>
    /// <remarks>
    /// This overload is intended for a CLI integration-test surface that should automatically include every
    /// options-based job in the supplied assemblies. A job that implements more than one closed
    /// <see cref="IHangfireJob{TOptions}"/> interface is skipped because one operation cannot infer which options
    /// type to expose. Use <see cref="RegisterWorkflowTests{TJobBase, TOptions}"/> for such a job.
    /// </remarks>
    public static OperationCatalogBuilder RegisterAllOptionsJobs(
        this OperationCatalogBuilder builder,
        IServiceProvider services,
        IEnumerable<Assembly> assemblies,
        Action<HangfireWorkflowTestRegistrationOptions>? configure = null)
    {
        ThrowIfNull(builder);
        ThrowIfNull(services);
        ThrowIfNull(assemblies);

        var options = new HangfireWorkflowTestRegistrationOptions();
        configure?.Invoke(options);
        Validate(options);

        var assemblyList = assemblies.ToArray();
        if (assemblyList.Any(static assembly => assembly is null))
        {
            throw new ArgumentException("Assemblies must not contain null values.", nameof(assemblies));
        }

        var candidates = new List<(Type JobType, Type OptionsInterface)>();
        foreach (var jobType in GetLoadableTypes(assemblyList, options).Where(IsConcreteClosedClass).Distinct())
        {
            // Check Exclude predicate first, before inspecting interfaces, so an excluded type
            // is skipped silently and never triggers an ambiguity report.
            if (options.Exclude?.Invoke(jobType) == true)
            {
                Report(options, jobType, "The job type was excluded by the configured predicate.", HangfireJobDiscoveryDisposition.Skipped);
                continue;
            }

            var optionsInterfaces = jobType.GetInterfaces()
                .Where(static candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IHangfireJob<>))
                .Distinct()
                .ToArray();

            if (optionsInterfaces.Length > 1)
            {
                Report(options, jobType, "Multiple closed IHangfireJob<TOptions> interfaces were found; use RegisterWorkflowTests<TJobBase, TOptions> to register this job explicitly.", HangfireJobDiscoveryDisposition.Skipped, failInStrictMode: true);
                continue;
            }

            if (optionsInterfaces.Length == 1)
            {
                candidates.Add((jobType, optionsInterfaces[0]));
            }
        }

        foreach (var (jobType, optionsInterface) in candidates
                     .OrderBy(pair => NormalizeName(options.NameFactory?.Invoke(pair.JobType) ?? ToKebabCase(pair.JobType.Name), options.Category), StringComparer.Ordinal)
                     .ThenBy(pair => pair.JobType.FullName, StringComparer.Ordinal))
        {
            var method = SelectMethod(jobType, optionsInterface, options);
            var name = options.NameFactory?.Invoke(jobType) ?? ToKebabCase(jobType.Name);
            var runner = new WorkflowTestRunner(services, jobType, method, options);
            var implementation = (Delegate)CreateWorkflowTestDelegateMethod
                .MakeGenericMethod(optionsInterface.GetGenericArguments()[0])
                .Invoke(null, [runner])!;

            builder.Add(name, $"Runs {jobType.FullName ?? jobType.Name} directly and captures its log transcript.", implementation, registration =>
            {
                registration.Category = options.Category;
                registration.SafetyLevel = options.SafetyLevel;
            });

            Report(options, jobType, "The job type was registered.", HangfireJobDiscoveryDisposition.Registered, method.Name, name);
        }

        return builder;
    }

    /// <summary>
    /// Discovers every concrete class across <paramref name="assemblies"/> marked with <see cref="HangfireJobAttribute"/>
    /// and registers a direct workflow-test operation for each one whose public <c>Execute</c>/<c>ExecuteAsync</c>
    /// method matches the structurally-typed parameterless or <c>(TOptions, CancellationToken)</c> shape — without
    /// requiring the type to implement <see cref="IHangfireJob"/> or <see cref="IHangfireJob{TOptions}"/> at all.
    /// This is the attribute-based, duck-typed counterpart to
    /// <see cref="RegisterWorkflowTests{TJobBase}"/>/<see cref="RegisterWorkflowTests{TJobBase, TOptions}"/>/
    /// <see cref="RegisterAllOptionsJobs(OperationCatalogBuilder, IServiceProvider, IEnumerable{Assembly}, Action{HangfireWorkflowTestRegistrationOptions}?)"/>:
    /// it lets a pre-existing job base class that already implements its own, unrelated marker interface adopt
    /// discovery by adding <c>[HangfireJob]</c> instead of changing its interface list. <see cref="HangfireJobAttribute"/>
    /// is <see cref="AttributeUsageAttribute.Inherited"/>, so annotating a shared base class is enough to make every
    /// concrete subclass discoverable. A job exposing more than one differently-shaped execution method (for
    /// example, both a parameterless and an options-based method) is skipped and reported as ambiguous; annotate it
    /// with <c>[HangfireJob(typeof(TOptions))]</c> or configure a <see cref="HangfireWorkflowTestRegistrationOptions.MethodSelector"/>
    /// to disambiguate.
    /// </summary>
    public static OperationCatalogBuilder RegisterAttributeWorkflowTests(
        this OperationCatalogBuilder builder,
        IServiceProvider services,
        IEnumerable<Assembly> assemblies,
        Action<HangfireWorkflowTestRegistrationOptions>? configure = null)
    {
        ThrowIfNull(builder);
        ThrowIfNull(services);
        ThrowIfNull(assemblies);

        var options = new HangfireWorkflowTestRegistrationOptions();
        configure?.Invoke(options);
        Validate(options);

        var assemblyList = assemblies.ToArray();
        if (assemblyList.Any(static assembly => assembly is null))
        {
            throw new ArgumentException("Assemblies must not contain null values.", nameof(assemblies));
        }

        var candidates = new List<(Type JobType, MethodInfo Method, Type? OptionsType)>();
        foreach (var jobType in GetLoadableTypes(assemblyList, options).Where(IsConcreteClosedClass).Distinct())
        {
            var attribute = HangfireAttributeJobDiscovery.GetAttribute(jobType);
            if (attribute is null)
            {
                continue;
            }

            if (options.Exclude?.Invoke(jobType) == true)
            {
                Report(options, jobType, "The job type was excluded by the configured predicate.", HangfireJobDiscoveryDisposition.Skipped);
                continue;
            }

            var selection = HangfireAttributeJobDiscovery.SelectExecutionMethod(jobType, attribute.OptionsType, options.MethodSelector);
            if (selection.Method is null)
            {
                Report(options, jobType, selection.Reason!, HangfireJobDiscoveryDisposition.Skipped, failInStrictMode: true);
                continue;
            }

            if (selection.MultipleCandidates)
            {
                Report(options, jobType, selection.Reason!, HangfireJobDiscoveryDisposition.Warning, failInStrictMode: true);
            }

            candidates.Add((jobType, selection.Method, selection.OptionsType));
        }

        foreach (var (jobType, method, optionsType) in candidates
                     .OrderBy(candidate => NormalizeName(options.NameFactory?.Invoke(candidate.JobType) ?? ToKebabCase(candidate.JobType.Name), options.Category), StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.JobType.FullName, StringComparer.Ordinal))
        {
            var name = options.NameFactory?.Invoke(jobType) ?? ToKebabCase(jobType.Name);
            var runner = new WorkflowTestRunner(services, jobType, method, options);
            var description = $"Runs {jobType.FullName ?? jobType.Name} directly and captures its log transcript.";

            Delegate implementation = optionsType is null
                ? (Func<CancellationToken, Task<HangfireWorkflowTestResult>>)runner.RunAsync
                : (Delegate)CreateWorkflowTestDelegateMethod.MakeGenericMethod(optionsType).Invoke(null, [runner])!;

            builder.Add(name, description, implementation, registration =>
            {
                registration.Category = options.Category;
                registration.SafetyLevel = options.SafetyLevel;
            });

            Report(options, jobType, "The job type was registered.", HangfireJobDiscoveryDisposition.Registered, method.Name, name);
        }

        return builder;
    }

    private static readonly MethodInfo CreateWorkflowTestDelegateMethod =
        typeof(HangfireWorkflowTestCatalogBuilderExtensions).GetMethod(nameof(CreateWorkflowTestDelegate), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Func<TOptions, CancellationToken, Task<HangfireWorkflowTestResult>> CreateWorkflowTestDelegate<TOptions>(WorkflowTestRunner runner)
        => runner.RunAsync<TOptions>;

    private static void Report(
        HangfireWorkflowTestRegistrationOptions options,
        Type? jobType,
        string message,
        HangfireJobDiscoveryDisposition disposition = HangfireJobDiscoveryDisposition.Warning,
        string? method = null,
        string? operationName = null,
        bool failInStrictMode = false)
    {
        var effectiveDisposition = options.StrictValidation && failInStrictMode
            ? HangfireJobDiscoveryDisposition.Failed
            : disposition;

        options.DiscoveryReports.Add(new HangfireJobDiscoveryReport(
            jobType?.Assembly,
            jobType,
            message,
            method,
            operationName,
            effectiveDisposition,
            options.StrictValidation));

        if (disposition is not HangfireJobDiscoveryDisposition.Registered)
        {
            options.Diagnostics.Add(new HangfireJobRegistrationDiagnostic(jobType, message));
        }

        if (options.StrictValidation && failInStrictMode)
        {
            throw new OperationCatalogException($"Hangfire workflow test discovery failed for '{jobType?.FullName ?? "assembly"}': {message}");
        }
    }

    private static void Validate(HangfireWorkflowTestRegistrationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Category))
        {
            throw new ArgumentException("The workflow test category cannot be empty.", nameof(options.Category));
        }
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Timeout), "The workflow test timeout must be positive.");
        }
    }

    private static MethodInfo SelectMethod(Type jobType, Type jobBaseType, HangfireWorkflowTestRegistrationOptions options)
    {
        var method = options.MethodSelector?.Invoke(jobType)
            ?? jobType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name is "ExecuteAsync" or "Execute")
                .Where(method => IsValidExecutionMethod(method, jobType, jobBaseType))
                .OrderByDescending(method => method.Name == "ExecuteAsync")
                .ThenBy(method => method.MetadataToken)
                .FirstOrDefault();

        return method is not null && IsValidExecutionMethod(method, jobType, jobBaseType)
            ? method
            : throw new InvalidOperationException($"No supported public ExecuteAsync or Execute method was found on Hangfire job '{jobType.FullName}'.");
    }

    private static bool IsValidExecutionMethod(MethodInfo method, Type jobType, Type jobBaseType)
    {
        if (!method.IsPublic || method.IsStatic || method.ContainsGenericParameters || method.DeclaringType is null ||
            !method.DeclaringType.IsAssignableFrom(jobType) || !jobBaseType.IsAssignableFrom(method.DeclaringType) ||
            (method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask)))
        {
            return false;
        }

        var parameters = method.GetParameters();
        var optionsType = FindOptionsType(jobBaseType);
        return optionsType is null
            ? parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken)
            : parameters.Length == 2 && parameters[0].ParameterType == optionsType && parameters[1].ParameterType == typeof(CancellationToken);
    }

    private static Type? FindOptionsType(Type jobBaseType)
    {
        var interfaceType = jobBaseType.GetInterfaces().Append(jobBaseType)
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IHangfireJob<>));
        return interfaceType?.GetGenericArguments()[0];
    }

    // The closed-generic RegisterWorkflowTests overloads document that they always throw on an invalid method
    // and do not consult StrictValidation/DiscoveryReports, so they use this non-reporting variant and keep
    // their existing behavior of silently continuing with only the loadable types.
    private static IEnumerable<Type> GetLoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            if (assembly is null)
            {
                throw new ArgumentException("The supplied assemblies enumerable contains a null entry.", nameof(assemblies));
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(static type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                yield return type;
            }
        }
    }

    // RegisterAllOptionsJobs and RegisterAttributeWorkflowTests consult StrictValidation/DiscoveryReports, so
    // this variant mirrors the enqueue-oriented discovery and reports an assembly-load failure (honoring strict
    // mode) instead of silently continuing with only the loadable types.
    private static IEnumerable<Type> GetLoadableTypes(IEnumerable<Assembly> assemblies, HangfireWorkflowTestRegistrationOptions options)
    {
        foreach (var assembly in assemblies.Distinct())
        {
            if (assembly is null)
            {
                throw new ArgumentException("The supplied assemblies enumerable contains a null entry.", nameof(assemblies));
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                Report(
                    options,
                    null,
                    $"Could not load all types from assembly '{assembly.FullName}': {exception.Message}",
                    HangfireJobDiscoveryDisposition.Warning,
                    failInStrictMode: true);
                types = exception.Types.Where(static type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                yield return type;
            }
        }
    }

    private static bool IsConcreteClosedClass(Type type) => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters;

    private static string NormalizeName(string name, string category) => $"{category}\u001f{name}";

    private static string ToKebabCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 && (char.IsLower(value[index - 1]) || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    private sealed class WorkflowTestRunner(IServiceProvider services, Type jobType, MethodInfo method, HangfireWorkflowTestRegistrationOptions options)
    {
        public Task<HangfireWorkflowTestResult> RunAsync(CancellationToken cancellationToken) => RunAsync<object?>(null, cancellationToken);

        public async Task<HangfireWorkflowTestResult> RunAsync<TOptions>(TOptions workflowOptions, CancellationToken cancellationToken)
        {
            var transcript = new StringBuilder();
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(new TranscriptLoggerProvider(transcript)));
            var scope = new WorkflowTestServiceProvider(services, loggerFactory);
            using var timeout = new CancellationTokenSource(options.Timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var stopwatch = Stopwatch.StartNew();
            HangfireWorkflowTestStatus status;
            string? error = null;

            try
            {
                var job = scope.GetService(jobType) ?? ActivatorUtilities.CreateInstance(scope, jobType);
                var arguments = method.GetParameters().Length == 1
                    ? [linkedCancellation.Token]
                    : new object?[] { workflowOptions, linkedCancellation.Token };
                var invocation = method.Invoke(job, arguments);
                if (invocation is Task task)
                {
                    await task.ConfigureAwait(false);
                }
                else if (invocation is ValueTask valueTask)
                {
                    await valueTask.ConfigureAwait(false);
                }

                status = HangfireWorkflowTestStatus.Succeeded;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                status = HangfireWorkflowTestStatus.TimedOut;
                error = $"The workflow test exceeded its {options.Timeout} timeout.";
            }
            catch (OperationCanceledException)
            {
                status = HangfireWorkflowTestStatus.Cancelled;
                error = "The workflow test was cancelled.";
            }
            catch (Exception exception)
            {
                var actual = exception is TargetInvocationException { InnerException: not null } ? exception.InnerException : exception;
                status = HangfireWorkflowTestStatus.Failed;
                error = actual.ToString();
            }

            stopwatch.Stop();
            var transcriptText = transcript.ToString();
            var artifactPath = WriteArtifact(transcriptText);
            return new HangfireWorkflowTestResult(jobType.FullName ?? jobType.Name, status, stopwatch.Elapsed, transcriptText, artifactPath, error);
        }

        private string? WriteArtifact(string transcript)
        {
            if (string.IsNullOrWhiteSpace(options.ArtifactDirectory))
            {
                return null;
            }

            Directory.CreateDirectory(options.ArtifactDirectory);
            var path = Path.Combine(options.ArtifactDirectory, $"{ToKebabCase(jobType.Name)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.log");
            File.WriteAllText(path, transcript);
            return path;
        }
    }

    // Does not own loggerFactory; the caller (WorkflowTestRunner.RunAsync) creates and disposes it via its own
    // `using` declaration, so this wrapper does not implement IDisposable.
    private sealed class WorkflowTestServiceProvider(IServiceProvider inner, ILoggerFactory loggerFactory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoggerFactory)
            ? loggerFactory
            : serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(ILogger<>)
                // ActivatorUtilities.CreateInstance/constructor injection require the closed ILogger<T> type
                // itself (not merely something assignable to it), so wrap the factory-created logger in the
                // concrete Logger<T> the framework normally injects instead of returning a plain ILogger.
                ? Activator.CreateInstance(typeof(Logger<>).MakeGenericType(serviceType.GetGenericArguments()[0]), loggerFactory)
                : inner.GetService(serviceType);
    }

    private sealed class TranscriptLoggerProvider(StringBuilder transcript) : ILoggerProvider
    {
        private readonly object _lock = new();

        public ILogger CreateLogger(string categoryName) => new TranscriptLogger(categoryName, transcript, _lock);
        public void Dispose() { }
    }

    private sealed class TranscriptLogger(string categoryName, StringBuilder transcript, object transcriptLock) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (transcriptLock)
            {
                transcript.Append(DateTimeOffset.UtcNow.ToString("O"))
                    .Append(' ').Append(logLevel).Append(' ').Append(categoryName).Append(": ")
                    .AppendLine(formatter(state, exception));
                if (exception is not null)
                {
                    transcript.AppendLine(exception.ToString());
                }
            }
        }
    }
}
