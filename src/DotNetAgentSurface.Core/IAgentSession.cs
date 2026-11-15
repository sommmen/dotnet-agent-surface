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

#if NET
    /// <summary>
    /// Returns the value stored under <paramref name="key"/>, creating it with <paramref name="factory"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Use it for create-once values such as a signing key. <see cref="InMemoryAgentSession"/> runs it atomically in
    /// process. A file-backed session runs it under a cross-process lock against freshly read state and <b>persists
    /// immediately</b> (it does not wait for <c>Save</c>), so concurrent CLI processes converge on one value. The default
    /// implementation is a non-atomic <see cref="Get{T}(string)"/> then <see cref="Set{T}(string, T)"/>; it persists whenever
    /// the implementation does.
    /// </remarks>
    T GetOrAdd<T>(string key, Func<T> factory)
    {
        if (Get<T>(key) is { } existing)
        {
            return existing;
        }

        var created = factory();
        Set(key, created);
        return created;
    }

    /// <summary>
    /// Replaces the value under <paramref name="key"/> with <c>update(current)</c> (<c>current</c> is <see langword="default"/>
    /// when there is none) and returns it.
    /// </summary>
    /// <remarks>
    /// Use it for read-modify-write of a single key, such as adding an entry to a dictionary value. A file-backed session
    /// runs it under a cross-process lock against freshly read state and <b>persists immediately</b>, so concurrent CLI
    /// processes never lose each other's entries. The delegate runs while the lock is held: keep it quick and
    /// side-effect free. The default implementation is a non-atomic <see cref="Get{T}(string)"/> then
    /// <see cref="Set{T}(string, T)"/>.
    /// </remarks>
    T Update<T>(string key, Func<T?, T> update)
    {
        var updated = update(Get<T>(key));
        Set(key, updated);
        return updated;
    }
#endif
}

/// <summary>
/// <c>GetOrAdd</c> and <c>Update</c> for <see cref="IAgentSession"/> on targets without default interface members
/// (<c>netstandard2.0</c>). It is compiled for every target so the package surface is identical; on <c>net10.0</c> the
/// interface members take precedence. <see cref="InMemoryAgentSession"/> (and file-backed subclasses) use their atomic
/// implementation; any other session falls back to a non-atomic get-then-set.
/// </summary>
public static class AgentSessionExtensions
{
    /// <summary>Returns the value under <paramref name="key"/>, creating it with <paramref name="factory"/> when there is none; atomic for <see cref="InMemoryAgentSession"/> sessions, a non-atomic get-then-set otherwise.</summary>
    public static T GetOrAdd<T>(this IAgentSession session, string key, Func<T> factory)
    {
        Guard.ThrowIfNull(session);
        Guard.ThrowIfNull(factory);
        if (session is InMemoryAgentSession atomic)
        {
            return atomic.GetOrAdd(key, factory);
        }

        if (session.Get<T>(key) is { } existing)
        {
            return existing;
        }

        var created = factory();
        session.Set(key, created);
        return created;
    }

    /// <summary>Replaces the value under <paramref name="key"/> with <c>update(current)</c>; atomic for <see cref="InMemoryAgentSession"/> sessions, a non-atomic get-then-set otherwise.</summary>
    public static T Update<T>(this IAgentSession session, string key, Func<T?, T> update)
    {
        Guard.ThrowIfNull(session);
        Guard.ThrowIfNull(update);
        if (session is InMemoryAgentSession atomic)
        {
            return atomic.Update(key, update);
        }

        var updated = update(session.Get<T>(key));
        session.Set(key, updated);
        return updated;
    }
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