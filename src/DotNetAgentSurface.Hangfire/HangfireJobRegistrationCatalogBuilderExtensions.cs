using System.Reflection;
using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Adds on-demand operations that enqueue concrete <see cref="IHangfireJob"/> implementations, including
/// <see cref="HangfireJob"/> subclasses.
/// </summary>
public static class HangfireJobRegistrationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers concrete <typeparamref name="TJobBase"/> implementations and adds a parameterless enqueue
    /// operation for each. <typeparamref name="TJobBase"/> may be <see cref="HangfireJob"/> (or a subclass of it)
    /// for greenfield job classes, or the <see cref="IHangfireJob"/> interface itself — or any pre-existing
    /// base class/interface that implements it — to adopt a brownfield job hierarchy that cannot derive from
    /// <see cref="HangfireJob"/> (for example, one with constructor parameters or a CRTP-style generic
    /// self-reference). Discovery only inspects types via reflection; it never constructs a job instance, so
    /// brownfield job types may declare any constructor — Hangfire's own <see cref="JobActivator"/> creates the
    /// instance when the enqueued job actually executes.
    /// </summary>
    public static OperationCatalogBuilder RegisterJobs<TJobBase>(
        this OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<Assembly> assemblies,
        Action<HangfireJobRegistrationOptions>? configure = null)
        where TJobBase : class, IHangfireJob
    {
        return Register(
            builder,
            backgroundJobClient,
            assemblies,
            configure,
            typeof(TJobBase),
            static (client, jobType, method) =>
            {
                void Enqueue(CancellationToken cancellationToken)
                {
                    client.Create(new Job(jobType, method, new object?[] { CancellationToken.None }), new EnqueuedState());
                }

                return (Action<CancellationToken>)Enqueue;
            });
    }

    /// <summary>
    /// Discovers concrete <typeparamref name="TJobBase"/> implementations and adds an enqueue operation for each.
    /// <typeparamref name="TJobBase"/> may be <see cref="HangfireJobWithOptions{TOptions}"/> (or a subclass of it)
    /// for greenfield job classes, or the <see cref="IHangfireJob{TOptions}"/> interface itself — or any
    /// pre-existing base class/interface that implements it — to adopt a brownfield job hierarchy (see
    /// <see cref="RegisterJobs{TJobBase}"/> for details and constructor-parameter support).
    /// </summary>
    public static OperationCatalogBuilder RegisterJobs<TJobBase, TOptions>(
        this OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<Assembly> assemblies,
        Action<HangfireJobRegistrationOptions>? configure = null)
        where TJobBase : class, IHangfireJob<TOptions>
    {
        return Register(
            builder,
            backgroundJobClient,
            assemblies,
            configure,
            typeof(TJobBase),
            static (client, jobType, method) =>
            {
                void Enqueue(TOptions options, CancellationToken cancellationToken)
                {
                    client.Create(new Job(jobType, method, new object?[] { options, CancellationToken.None }), new EnqueuedState());
                }

                return (Action<TOptions, CancellationToken>)Enqueue;
            });
    }

    private static OperationCatalogBuilder Register(
        OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<Assembly> assemblies,
        Action<HangfireJobRegistrationOptions>? configure,
        Type jobBaseType,
        Func<IBackgroundJobClient, Type, MethodInfo, Delegate> implementationFactory)
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

        if (jobBaseType is null)
        {
            throw new ArgumentNullException(nameof(jobBaseType));
        }

        if (implementationFactory is null)
        {
            throw new ArgumentNullException(nameof(implementationFactory));
        }

        var options = new HangfireJobRegistrationOptions();
        configure?.Invoke(options);

        var assemblyList = assemblies.ToArray();
        if (assemblyList.Any(static assembly => assembly is null))
        {
            throw new ArgumentException("Assemblies must not contain null values.", nameof(assemblies));
        }

        var jobTypes = GetLoadableTypes(assemblyList, options)
            .Where(type => IsConcreteClosedClass(type) && jobBaseType.IsAssignableFrom(type))
            .Distinct()
            .OrderBy(type => NormalizeName(options.NameFactory?.Invoke(type) ?? ToKebabCase(type.Name), options.Category), StringComparer.Ordinal)
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var jobType in jobTypes)
        {
            if (options.Exclude?.Invoke(jobType) == true)
            {
                Report(options, jobType, "The job type was excluded by the configured predicate.", HangfireJobDiscoveryDisposition.Skipped);
                continue;
            }

            var method = SelectMethod(jobType, jobBaseType, options);
            if (method is null)
            {
                continue;
            }

            var name = options.NameFactory?.Invoke(jobType) ?? ToKebabCase(jobType.Name);
            var description = $"Enqueues a single execution of {jobType.FullName ?? jobType.Name}.";
            var metadata = new HangfireJobRegistrationMetadata(name, description, options.Category, options.SafetyLevel);
            options.Enrich?.Invoke(jobType, metadata);
            if (string.IsNullOrWhiteSpace(metadata.Name))
            {
                throw new InvalidOperationException($"Metadata enrichment produced an empty operation name for '{jobType.FullName}'.");
            }

            if (options.SafetyLevel == AgentSafetyLevel.Dangerous && metadata.SafetyLevel != AgentSafetyLevel.Dangerous)
            {
                metadata.SafetyLevel = AgentSafetyLevel.Dangerous;
            }

            builder.Add(metadata.Name, metadata.Description, implementationFactory(backgroundJobClient, jobType, method), operation =>
            {
                operation.Category = metadata.Category;
                operation.SafetyLevel = metadata.SafetyLevel;
                operation.Aliases.AddRange(metadata.Aliases);
                operation.Examples.AddRange(metadata.Examples);
            });

            Report(options, jobType, "The job type was registered.", HangfireJobDiscoveryDisposition.Registered, method.Name, metadata.Name);
        }

        return builder;
    }

    private static MethodInfo? SelectMethod(Type jobType, Type jobBaseType, HangfireJobRegistrationOptions options)
    {
        var selected = options.MethodSelector?.Invoke(jobType);
        if (selected is not null)
        {
            if (IsValidExecutionMethod(selected, jobType, jobBaseType))
            {
                return selected;
            }

            Report(options, jobType, "The selected execution method must be a public instance Execute or ExecuteAsync method with the expected parameters.", HangfireJobDiscoveryDisposition.Skipped, failInStrictMode: true);
            return null;
        }

        var candidates = jobType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => (method.Name == "Execute" || method.Name == "ExecuteAsync") && IsValidExecutionMethod(method, jobType, jobBaseType))
            .OrderBy(method => method.Name == "ExecuteAsync" ? 0 : 1)
            .ThenBy(method => method.MetadataToken)
            .ToArray();

        if (candidates.Length == 0)
        {
            Report(options, jobType, "No valid public Execute or ExecuteAsync method was found.", HangfireJobDiscoveryDisposition.Skipped, failInStrictMode: true);
            return null;
        }

        if (candidates.Length > 1)
        {
            Report(options, jobType, "Multiple valid Execute or ExecuteAsync methods were found; the deterministic conventional method was selected.", HangfireJobDiscoveryDisposition.Warning, failInStrictMode: true);
        }

        return candidates[0];
    }

    private static bool IsValidExecutionMethod(MethodInfo method, Type jobType, Type jobBaseType)
    {
        if (!method.IsPublic || method.IsStatic || method.ContainsGenericParameters || !method.DeclaringType!.IsAssignableFrom(jobType))
        {
            return false;
        }

        var parameters = method.GetParameters();
        var optionsBase = FindOptionsBase(jobBaseType);
        if (optionsBase is not null)
        {
            var optionType = optionsBase.GetGenericArguments()[0];
            return parameters.Length == 2 && parameters[0].ParameterType == optionType && parameters[1].ParameterType == typeof(CancellationToken);
        }

        return parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken);
    }

    private static IEnumerable<Type> GetLoadableTypes(IEnumerable<Assembly> assemblies, HangfireJobRegistrationOptions options)
    {
        var types = new List<Type>();
        foreach (var assembly in assemblies.Distinct())
        {
            try
            {
                types.AddRange(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                Report(
                    options,
                    null,
                    $"Could not load all types from assembly '{assembly.FullName}': {exception.Message}",
                    HangfireJobDiscoveryDisposition.Warning,
                    failInStrictMode: true);
                types.AddRange(exception.Types.Where(static type => type is not null).Cast<Type>());
            }
        }

        return types;
    }

    private static Type? FindOptionsBase(Type jobBaseType)
    {
        if (jobBaseType.IsGenericType && jobBaseType.GetGenericTypeDefinition() == typeof(IHangfireJob<>))
        {
            return jobBaseType;
        }

        for (var current = jobBaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(HangfireJobWithOptions<>))
            {
                return current;
            }
        }

        var optionsInterface = jobBaseType.GetInterfaces()
            .FirstOrDefault(static candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IHangfireJob<>));
        return optionsInterface;
    }

    private static bool IsConcreteClosedClass(Type type) => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters;

    private static void Report(
        HangfireJobRegistrationOptions options,
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
            throw new OperationCatalogException($"Hangfire job discovery failed for '{jobType?.FullName ?? "assembly"}': {message}");
        }
    }

    private static string NormalizeName(string name, string? category) => string.Concat(category ?? string.Empty, "/", name).ToLowerInvariant();

    private static string ToKebabCase(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current) && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1]) || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }
}
