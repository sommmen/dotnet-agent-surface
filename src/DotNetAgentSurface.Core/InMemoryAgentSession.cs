using System.Text.Json;

namespace DotNetAgentSurface.Core;

/// <summary>
/// A thread-safe, in-memory <see cref="IAgentSession"/>. Hosts register one per MCP session (process lifetime for
/// stdio, one per transport session for HTTP); it never touches disk.
/// </summary>
/// <remarks>Values are held as JSON so behaviour matches persisted sessions.</remarks>
public class InMemoryAgentSession : IAgentSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _changes = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>Creates a session with the supplied <paramref name="id"/>, or a new random one.</summary>
    public InMemoryAgentSession(string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id!;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Gets whether any key changed since the session was created or the changes were last marked persisted.</summary>
    protected bool IsModified
    {
        get
        {
            lock (_gate)
            {
                return _changes.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public T? Get<T>(string key)
    {
        Guard.ThrowIfNull(key);
        JsonElement element;
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out element))
            {
                return default;
            }
        }

        return Deserialize<T>(element);
    }

    /// <inheritdoc />
    public void Set<T>(string key, T value)
    {
        Guard.ThrowIfNull(key);
        var element = JsonSerializer.SerializeToElement(value);
        lock (_gate)
        {
            _values[key] = element;
            _changes[key] = ++_version;
        }
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        Guard.ThrowIfNull(key);
        lock (_gate)
        {
            if (_values.Remove(key))
            {
                _changes[key] = ++_version;
            }
        }
    }

    /// <inheritdoc cref="IAgentSession.GetOrAdd{T}(string, Func{T})" />
    public virtual T GetOrAdd<T>(string key, Func<T> factory)
    {
        Guard.ThrowIfNull(key);
        Guard.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var element) && Deserialize<T>(element) is { } existing)
            {
                return existing;
            }

            var created = factory();
            Set(key, created);
            return created;
        }
    }

    /// <inheritdoc cref="IAgentSession.Update{T}(string, Func{T, T})" />
    public virtual T Update<T>(string key, Func<T?, T> update)
    {
        Guard.ThrowIfNull(key);
        Guard.ThrowIfNull(update);
        lock (_gate)
        {
            var current = _values.TryGetValue(key, out var element) ? Deserialize<T>(element) : default;
            var updated = update(current);
            Set(key, updated);
            return updated;
        }
    }

    /// <summary>Returns a copy of the current values.</summary>
    protected IReadOnlyDictionary<string, JsonElement> Snapshot() => Snapshot(out _);

    /// <summary>Returns a copy of the current values and the modification version it reflects, for <see cref="MarkClean(long)"/>.</summary>
    protected IReadOnlyDictionary<string, JsonElement> Snapshot(out long version)
    {
        lock (_gate)
        {
            version = _version;
            return new Dictionary<string, JsonElement>(_values, StringComparer.Ordinal);
        }
    }

    /// <summary>Replaces all values and marks the session clean.</summary>
    protected void Replace(IEnumerable<KeyValuePair<string, JsonElement>> values)
    {
        Guard.ThrowIfNull(values);
        lock (_gate)
        {
            _values.Clear();
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value.Clone();
            }

            _changes.Clear();
            _version++;
        }
    }

    /// <summary>Marks the current state as persisted.</summary>
    protected void MarkClean()
    {
        lock (_gate)
        {
            _changes.Clear();
        }
    }

    /// <summary>Marks changes made up to <paramref name="version"/> (as returned by <see cref="Snapshot(out long)"/> or <see cref="SnapshotChanges"/>) as persisted; later changes stay pending.</summary>
    protected void MarkClean(long version)
    {
        lock (_gate)
        {
            foreach (var key in _changes.Where(pair => pair.Value <= version).Select(static pair => pair.Key).ToList())
            {
                _changes.Remove(key);
            }
        }
    }

    /// <summary>
    /// Returns the keys changed since they were last persisted and the version they reflect: the new value for a key that
    /// was set, <see langword="null"/> for a key that was removed.
    /// </summary>
    protected IReadOnlyDictionary<string, JsonElement?> SnapshotChanges(out long version)
    {
        lock (_gate)
        {
            version = _version;
            var changes = new Dictionary<string, JsonElement?>(_changes.Count, StringComparer.Ordinal);
            foreach (var key in _changes.Keys)
            {
                changes[key] = _values.TryGetValue(key, out var element) ? element : null;
            }

            return changes;
        }
    }

    /// <summary>Gets whether <paramref name="key"/> has a pending change, and its pending value (<see langword="null"/> when it was removed).</summary>
    protected bool TryGetChange(string key, out JsonElement? value)
    {
        lock (_gate)
        {
            if (_changes.ContainsKey(key))
            {
                value = _values.TryGetValue(key, out var element) ? element : null;
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>Stores a value that is already persisted under <paramref name="key"/>, clearing any pending change for it.</summary>
    protected void SetPersisted(string key, JsonElement value)
    {
        lock (_gate)
        {
            _values[key] = value.Clone();
            _changes.Remove(key);
        }
    }

    /// <summary>
    /// Adopts the persisted <paramref name="values"/> as the current state and marks changes up to <paramref name="version"/>
    /// persisted; keys changed after <paramref name="version"/> keep their local value and stay pending.
    /// </summary>
    protected void ApplyPersisted(IReadOnlyDictionary<string, JsonElement> values, long version)
    {
        Guard.ThrowIfNull(values);
        lock (_gate)
        {
            var merged = new Dictionary<string, JsonElement>(values.Count, StringComparer.Ordinal);
            foreach (var pair in values)
            {
                merged[pair.Key] = pair.Value.Clone();
            }

            foreach (var change in _changes.ToList())
            {
                if (change.Value > version)
                {
                    if (_values.TryGetValue(change.Key, out var local))
                    {
                        merged[change.Key] = local;
                    }
                    else
                    {
                        merged.Remove(change.Key);
                    }
                }
                else
                {
                    _changes.Remove(change.Key);
                }
            }

            _values.Clear();
            foreach (var pair in merged)
            {
                _values[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>Deserializes a stored value, returning <see langword="default"/> for JSON <c>null</c> or a value that does not fit <typeparamref name="T"/>.</summary>
    protected static T? Deserialize<T>(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        try
        {
            return element.Deserialize<T>();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
