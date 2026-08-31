namespace DotNetAgentSurface.Core;

/// <summary>
/// Fluent, EF Core-style builder that composes an <see cref="OperationCatalog"/> from attributed service
/// types (see <see cref="OperationCatalog.Discover"/>) and explicit delegate-based registrations. Delegate
/// registrations are useful when a method cannot carry an <see cref="AgentOperationAttribute"/> (third-party
/// types) or when an operation is assembled ad hoc at runtime.
/// </summary>
public sealed class OperationCatalogBuilder
{
    private readonly List<OperationDescriptor> _operations = [];

    /// <summary>
    /// Discovers every <see cref="AgentOperationAttribute"/>-annotated public method on <paramref name="serviceType"/>,
    /// identical to how <see cref="OperationCatalog.Discover"/> treats a single type.
    /// </summary>
    public OperationCatalogBuilder AddFromType(Type serviceType)
    {
        Guard.ThrowIfNull(serviceType);

        _operations.AddRange(OperationCatalog.DiscoverOperations(serviceType));
        return this;
    }

    /// <summary>
    /// Generic convenience overload of <see cref="AddFromType(Type)"/>.
    /// </summary>
    public OperationCatalogBuilder AddFromType<TService>() => AddFromType(typeof(TService));

    /// <summary>
    /// Registers an operation directly from a delegate rather than an attributed method. The delegate's
    /// parameters become the operation's parameters, and its underlying <see cref="Delegate.Method"/> feeds
    /// the same invocation pipeline used by attribute-discovered operations (<see cref="OperationInvoker"/>
    /// invokes <c>operation.Method.Invoke(target, arguments)</c> unchanged). A static-method delegate needs no
    /// service resolution; an instance-method delegate requires an instance of its declaring type to be
    /// resolvable from the invoker's <see cref="IServiceProvider"/>, exactly like attribute discovery.
    /// </summary>
    public OperationCatalogBuilder Add(
        string name,
        string description,
        Delegate implementation,
        Action<OperationRegistrationOptions>? configure = null)
    {
        Guard.ThrowIfNullOrWhiteSpace(name);
        Guard.ThrowIfNullOrWhiteSpace(description);
        Guard.ThrowIfNull(implementation);

        var options = new OperationRegistrationOptions();
        configure?.Invoke(options);

        var attribute = new AgentOperationAttribute(name, description)
        {
            Category = options.Category,
            SafetyLevel = options.SafetyLevel,
            Examples = [.. options.Examples],
            Aliases = [.. options.Aliases]
        };

        _operations.Add(OperationCatalog.CreateDescriptor(implementation.Method, attribute));
        return this;
    }

    /// <summary>
    /// Sorts, validates, and finalizes the registered operations into an <see cref="OperationCatalog"/>,
    /// applying the same ordering and name/alias uniqueness guarantees as <see cref="OperationCatalog.Discover"/>.
    /// </summary>
    public OperationCatalog Build() => OperationCatalog.CreateCatalog(_operations);
}

/// <summary>
/// Fluent configuration surface for <see cref="OperationCatalogBuilder.Add"/>, mirroring the optional
/// properties available on <see cref="AgentOperationAttribute"/>.
/// </summary>
public sealed class OperationRegistrationOptions
{
    public string? Category { get; set; }

    public AgentSafetyLevel SafetyLevel { get; set; } = AgentSafetyLevel.Safe;

    public List<string> Examples { get; } = [];

    public List<string> Aliases { get; } = [];
}
