using System.Reflection;
using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Adds operations for class-based Hangfire jobs that are discovered via reflection at application startup.
/// </summary>
public static class HangfireJobTypeOperationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers concrete class-based jobs from <paramref name="assemblies"/> and adds an operation for each
    /// matching type that enqueues a single background execution of that job via
    /// <paramref name="backgroundJobClient"/>. Discovery does not register, modify, or depend on any recurring
    /// job configuration — it only makes the job invokable on demand, exactly like calling
    /// <c>BackgroundJob.Enqueue</c> for it. The supplied <paramref name="jobMethod"/> must identify an
    /// instance method whose arguments can be supplied by <paramref name="argumentsFactory"/>. Supplying a
    /// custom argument factory permits options-based jobs to be exposed without assuming their options can be
    /// constructed by this library.
    /// </summary>
    public static OperationCatalogBuilder AddHangfireJobTypes(
        this OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<Assembly> assemblies,
        Func<Type, bool> isJobType,
        Func<Type, MethodInfo> jobMethod,
        Func<Type, object?[]>? argumentsFactory = null,
        Action<HangfireJobTypeDiscoveryOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (backgroundJobClient is null)
        {
            throw new ArgumentNullException(nameof(backgroundJobClient));
        }

        if (assemblies is null)
        {
            throw new ArgumentNullException(nameof(assemblies));
        }

        if (isJobType is null)
        {
            throw new ArgumentNullException(nameof(isJobType));
        }

        if (jobMethod is null)
        {
            throw new ArgumentNullException(nameof(jobMethod));
        }

        var options = new HangfireJobTypeDiscoveryOptions();
        configure?.Invoke(options);

        var jobTypes = assemblies
            .Distinct()
            .SelectMany(GetLoadableTypes)
            .Where(IsConcreteClosedClass)
            .Where(isJobType)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var jobType in jobTypes)
        {
            var method = jobMethod(jobType)
                ?? throw new InvalidOperationException($"No job method was supplied for '{jobType.FullName}'.");
            var arguments = argumentsFactory?.Invoke(jobType) ?? [];
            var job = new Job(jobType, method, arguments);
            var operationName = options.OperationNameFactory?.Invoke(jobType) ?? jobType.FullName ?? jobType.Name;

            if (string.IsNullOrWhiteSpace(operationName))
            {
                throw new InvalidOperationException($"No operation name was supplied for '{jobType}'.");
            }

            var registration = new HangfireOperationRegistrationOptions();
            options.Enrich?.Invoke(jobType, registration);
            builder.Add(
                registration.Name ?? operationName,
                registration.Description ?? $"Enqueues a background execution of '{jobType.FullName}.{method.Name}'.",
                (Action)(() => backgroundJobClient.Create(job, new EnqueuedState())),
                operation =>
                {
                    operation.Category = registration.Category ?? options.Category;
                    operation.SafetyLevel = registration.SafetyLevel ?? options.SafetyLevel;
                    operation.Aliases.AddRange(registration.Aliases);
                    operation.Examples.AddRange(registration.Examples);
                });
        }

        return builder;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>();
        }
    }

    private static bool IsConcreteClosedClass(Type type) =>
        type.IsClass &&
        !type.IsAbstract &&
        !type.ContainsGenericParameters;
}
