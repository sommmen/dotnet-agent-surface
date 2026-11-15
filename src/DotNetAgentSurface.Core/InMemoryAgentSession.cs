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
    private bool _modified;
    private long _version;

    /// <summary>Creates a session with the supplied <paramref name="id"/>, or a new random one.</summary>
    public InMemoryAgentSession(string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id!;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Gets whether state changed since the session was created or <see cref="MarkClean()"/> was last called.</summary>
    protected bool IsModified
    {
        get
        {
            lock (_gate)
            {
                return _modified;
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

    /// <inheritdoc />
    public void Set<T>(string key, T value)
    {
        Guard.ThrowIfNull(key);
        var element = JsonSerializer.SerializeToElement(value);
        lock (_gate)
        {
            _values[key] = element;
            _modified = true;
            _version++;
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
                _modified = true;
                _version++;
            }
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

            _modified = false;
            _version++;
        }
    }

    /// <summary>Marks the current state as persisted.</summary>
    protected void MarkClean()
    {
        lock (_gate)
        {
            _modified = false;
        }
    }

    /// <summary>Marks the state as persisted only if nothing changed since <see cref="Snapshot(out long)"/> returned <paramref name="version"/>.</summary>
    protected void MarkClean(long version)
    {
        lock (_gate)
        {
            if (_version == version)
            {
                _modified = false;
            }
        }
    }
}
