using System.Reflection;

namespace DotNetAgentSurface.Hangfire;

/// <summary>
/// Shared reflection helpers used by both the enqueue-oriented (<see cref="HangfireJobRegistrationCatalogBuilderExtensions"/>)
/// and workflow-test-oriented (<see cref="HangfireWorkflowTestCatalogBuilderExtensions"/>) catalog builder
/// extensions to discover <see cref="HangfireJobAttribute"/>-marked job types and select their execution method by
/// structural (duck-typed) shape rather than by a closed <see cref="IHangfireJob{TOptions}"/> interface argument.
/// </summary>
internal static class HangfireAttributeJobDiscovery
{
    /// <summary>The outcome of selecting a structural execution method for an attribute-marked job type.</summary>
    public readonly struct ExecutionMethodSelection
    {
        /// <summary>Gets the selected method, or <see langword="null"/> when none was found or the result is ambiguous.</summary>
        public MethodInfo? Method { get; init; }

        /// <summary>Gets the options type inferred from <see cref="Method"/>'s first parameter, or <see langword="null"/> for a parameterless job.</summary>
        public Type? OptionsType { get; init; }

        /// <summary>Gets whether more than one differently-shaped candidate method was found (for example, both a parameterless and an options-based method).</summary>
        public bool Ambiguous { get; init; }

        /// <summary>Gets whether more than one same-shaped candidate method was found (for example, both <c>Execute</c> and <c>ExecuteAsync</c>).</summary>
        public bool MultipleCandidates { get; init; }

        /// <summary>Gets a human-readable explanation when <see cref="Method"/> is <see langword="null"/> or <see cref="MultipleCandidates"/> is <see langword="true"/>.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>Gets the <see cref="HangfireJobAttribute"/> applied to <paramref name="type"/> or inherited from a base class, if any.</summary>
    public static HangfireJobAttribute? GetAttribute(Type type) => type.GetCustomAttribute<HangfireJobAttribute>(inherit: true);

    /// <summary>
    /// Selects the execution method for an attribute-marked job type, honoring an explicit
    /// <paramref name="methodSelector"/> callback first (mirroring the <c>MethodSelector</c> option already
    /// present on both registration options types), then falling back to structural discovery of every public
    /// <c>Execute</c>/<c>ExecuteAsync</c> method matching the parameterless or <c>(TOptions, CancellationToken)</c>
    /// shape. A type exposing more than one differently-shaped candidate is reported as ambiguous, since a single
    /// registered operation cannot represent both a parameterless and an options-based execution.
    /// </summary>
    public static ExecutionMethodSelection SelectExecutionMethod(Type jobType, Type? explicitOptionsType, Func<Type, MethodInfo?>? methodSelector)
    {
        if (methodSelector?.Invoke(jobType) is { } selected)
        {
            var selectedOptionsType = InferOptionsType(selected);
            if (IsValidExecutionMethod(selected, jobType, selectedOptionsType) && (explicitOptionsType is null || explicitOptionsType == selectedOptionsType))
            {
                return new ExecutionMethodSelection { Method = selected, OptionsType = selectedOptionsType };
            }

            return new ExecutionMethodSelection
            {
                Reason = "The selected execution method must be a public instance Execute or ExecuteAsync method with the expected parameters.",
            };
        }

        var candidates = FindShapedMethods(jobType, explicitOptionsType);
        if (candidates.Count == 0)
        {
            return new ExecutionMethodSelection { Reason = "No valid public Execute or ExecuteAsync method was found." };
        }

        var distinctShapes = candidates.Select(static candidate => candidate.OptionsType).Distinct().ToArray();
        if (distinctShapes.Length > 1)
        {
            return new ExecutionMethodSelection
            {
                Ambiguous = true,
                Reason = "Multiple differently-shaped Execute/ExecuteAsync methods were found (for example, both a parameterless and an options-based method); specify [HangfireJob(typeof(TOptions))] or a MethodSelector to disambiguate.",
            };
        }

        var ordered = candidates
            .OrderBy(static candidate => candidate.Method.Name == "ExecuteAsync" ? 0 : 1)
            .ThenBy(static candidate => candidate.Method.MetadataToken)
            .ToArray();

        return new ExecutionMethodSelection
        {
            Method = ordered[0].Method,
            OptionsType = ordered[0].OptionsType,
            MultipleCandidates = ordered.Length > 1,
            Reason = ordered.Length > 1
                ? "Multiple valid Execute or ExecuteAsync methods were found; the deterministic conventional method was selected."
                : null,
        };
    }

    private static Type? InferOptionsType(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters switch
        {
            { Length: 1 } single when single[0].ParameterType == typeof(CancellationToken) => null,
            { Length: 2 } pair when pair[1].ParameterType == typeof(CancellationToken) => pair[0].ParameterType,
            _ => null,
        };
    }

    private static bool IsValidExecutionMethod(MethodInfo method, Type jobType, Type? optionsType)
    {
        if (method.Name is not ("Execute" or "ExecuteAsync"))
        {
            return false;
        }

        if (!method.IsPublic || method.IsStatic || method.ContainsGenericParameters || method.DeclaringType is null || !method.DeclaringType.IsAssignableFrom(jobType))
        {
            return false;
        }

        // A by-ref, pointer, or byref-like options type (for example, a method taking `ref`/`in`/`out TOptions`,
        // a pointer parameter, or a ref struct such as Span<T>) cannot be used as a generic type argument when
        // constructing the invocation delegate, so reject it here rather than letting an explicitly-selected
        // method reach that failure at catalog-construction time.
        if (!IsUsableAsGenericArgument(optionsType))
        {
            return false;
        }

        if (method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return optionsType is null
            ? parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken)
            : parameters.Length == 2 && parameters[0].ParameterType == optionsType && parameters[1].ParameterType == typeof(CancellationToken);
    }

    /// <summary>Gets whether <paramref name="type"/> can be used as a generic type argument (for example, when constructing a strongly-typed invocation delegate).</summary>
    private static bool IsUsableAsGenericArgument(Type? type) => type is null || (!type.IsByRef && !type.IsPointer && !IsByRefLike(type));

    /// <summary>
    /// Gets whether <paramref name="type"/> is a byref-like type (a `ref struct` such as <c>Span&lt;T&gt;</c>).
    /// Checked by the compiler-emitted "IsByRefLikeAttribute" marker's name rather than <c>Type.IsByRefLike</c> or
    /// <c>typeof(IsByRefLikeAttribute)</c>, neither of which is available on the netstandard2.0 target framework.
    /// </summary>
    private static bool IsByRefLike(Type type) =>
        type.CustomAttributes.Any(static attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");

    private static List<(MethodInfo Method, Type? OptionsType)> FindShapedMethods(Type jobType, Type? explicitOptionsType)
    {
        var matches = new List<(MethodInfo Method, Type? OptionsType)>();
        foreach (var method in jobType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.Name is not ("Execute" or "ExecuteAsync"))
            {
                continue;
            }

            if (method.IsStatic || method.ContainsGenericParameters || method.DeclaringType is null || !method.DeclaringType.IsAssignableFrom(jobType))
            {
                continue;
            }

            if (method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask))
            {
                continue;
            }

            var parameters = method.GetParameters();
            Type? optionsType;
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken))
            {
                optionsType = null;
            }
            else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(CancellationToken))
            {
                optionsType = parameters[0].ParameterType;
            }
            else
            {
                continue;
            }

            // A by-ref, pointer, or byref-like options type (`ref`/`in`/`out TOptions`, a pointer parameter, or a
            // ref struct such as Span<T>) cannot be used as a generic type argument when constructing the
            // invocation delegate, so it is not a valid structural candidate.
            if (!IsUsableAsGenericArgument(optionsType))
            {
                continue;
            }

            if (explicitOptionsType is not null && optionsType != explicitOptionsType)
            {
                continue;
            }

            matches.Add((method, optionsType));
        }

        return matches;
    }
}
