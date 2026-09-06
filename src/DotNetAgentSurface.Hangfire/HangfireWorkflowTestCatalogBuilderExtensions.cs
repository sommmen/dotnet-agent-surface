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

    private static IEnumerable<Type> GetLoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies.Distinct())
        {
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
            var path = Path.Combine(options.ArtifactDirectory, $"{ToKebabCase(jobType.Name)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
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
        public ILogger CreateLogger(string categoryName) => new TranscriptLogger(categoryName, transcript);
        public void Dispose() { }
    }

    private sealed class TranscriptLogger(string categoryName, StringBuilder transcript) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
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
