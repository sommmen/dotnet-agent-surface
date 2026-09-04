using System.Reflection;
using DotNetAgentSurface.Core;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Adds on-demand operations that enqueue concrete <see cref="HangfireJob"/> implementations.
/// </summary>
public static class HangfireJobRegistrationCatalogBuilderExtensions
{
    /// <summary>
    /// Discovers concrete <see cref="HangfireJob"/> implementations and adds a parameterless enqueue operation for each.
    /// </summary>
    public static OperationCatalogBuilder RegisterJobs<TJobBase>(
        this OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<Assembly> assemblies,
        Action<HangfireJobRegistrationOptions>? configure = null)
        where TJobBase : HangfireJob
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
    /// Discovers concrete <see cref="HangfireJobWithOptions{TOptions}"/> implementations and adds an enqueue operation for each.
    /// </summary>
    public static OperationCatalogBuilder RegisterJobs<TJobBase, TOptions>(
        this OperationCatalogBuilder builder,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<Assembly> assemblies,
        Action<HangfireJobRegistrationOptions>? configure = null)
        where TJobBase : HangfireJobWithOptions<TOptions>
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

        var jobTypes = GetLoadableTypes(assemblies, options)
            .Where(type => IsConcreteClosedClass(type) && jobBaseType.IsAssignableFrom(type))
            .Distinct()
            .OrderBy(type => NormalizeName(options.NameFactory?.Invoke(type) ?? ToKebabCase(type.Name), options.Category), StringComparer.Ordinal)
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var jobType in jobTypes)
        {
            if (options.Exclude?.Invoke(jobType) == true)
            {
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
            options.EnrichAsync?.Invoke(jobType, metadata).GetAwaiter().GetResult();
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

            Report(options, jobType, "The selected execution method must be a public instance Execute or ExecuteAsync method with the expected parameters.", failInStrictMode: true);
            return null;
        }

        var candidates = jobType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => (method.Name == "Execute" || method.Name == "ExecuteAsync") && IsValidExecutionMethod(method, jobType, jobBaseType))
            .OrderBy(method => method.Name == "ExecuteAsync" ? 0 : 1)
            .ThenBy(method => method.MetadataToken)
            .ToArray();

        if (candidates.Length == 0)
        {
            Report(options, jobType, "No valid public Execute or ExecuteAsync method was found.", failInStrictMode: true);
            return null;
        }

        if (candidates.Length > 1)
        {
            Report(options, jobType, "Multiple valid Execute or ExecuteAsync methods were found; the deterministic conventional method was selected.", failInStrictMode: true);
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
                Report(options, null, $"Could not load all types from assembly '{assembly.FullName}': {exception.Message}", failInStrictMode: true);
                types.AddRange(exception.Types.Where(static type => type is not null).Cast<Type>());
            }
        }

        return types;
    }

    private static Type? FindOptionsBase(Type jobBaseType)
    {
        for (var current = jobBaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(HangfireJobWithOptions<>))
            {
                return current;
            }
        }

        return null;
    }

    private static bool IsConcreteClosedClass(Type type) => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters;

    private static void Report(HangfireJobRegistrationOptions options, Type? jobType, string message, bool failInStrictMode = false)
    {
        options.Diagnostics.Add(new HangfireJobRegistrationDiagnostic(jobType, message));
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
