namespace DotNetAgentSurface.Core;

/// <summary>
/// Surface-neutral key/value state that outlives a single operation call, so stateful flows such as
/// <c>load_assembly</c> → <c>list_contexts</c> → <c>run_query</c> behave the same over MCP and over a CLI where every
/// invocation is a fresh process.
/// </summary>
/// <remarks>
/// <para>
/// Operations receive the session as an injected service (a constructor parameter of the operation class, or any
/// DI-resolved service); it is never an operation parameter and never appears in a schema.
/// </para>
/// <para>
/// <b>Never store secrets.</b> The CLI persists the session as plain JSON on disk. Keep connection strings, tokens and
/// passwords in configuration or user secrets and store only <i>intent</i>: names, identifiers and paths. Live objects
/// are rebuilt from that intent by an <see cref="IAgentSessionInitializer"/>.
/// </para>
/// <para>
/// Values are stored as JSON, so every surface sees the same behaviour: only JSON-serializable values round-trip.
/// </para>
/// </remarks>
public interface IAgentSession
{
    /// <summary>Gets the identifier of this session.</summary>
    string Id { get; }

    /// <summary>Gets the value stored under <paramref name="key"/>, or <see langword="default"/> when there is none.</summary>
    T? Get<T>(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing any previous value.</summary>
    void Set<T>(string key, T value);

    /// <summary>Removes the value stored under <paramref name="key"/>, if any.</summary>
    void Remove(string key);
}

/// <summary>
/// Rebuilds live state from a persisted <see cref="IAgentSession"/>. The CLI runs every registered initializer after
/// the host is built and before an operation is invoked; MCP servers are long-lived and do not need it.
/// </summary>
public interface IAgentSessionInitializer
{
    /// <summary>Restores live state, for example re-loading each assembly path recorded in <paramref name="session"/>.</summary>
    ValueTask InitializeAsync(IAgentSession session, CancellationToken cancellationToken);
}
