namespace DotNetAgentSurface.Core;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AgentOperationAttribute : Attribute
{
    public AgentOperationAttribute(string name) : this(name, null)
    {
    }

    public AgentOperationAttribute(string name, string? description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string? Description { get; }

    public string? Category { get; init; }

    /// <summary>
    /// Gets the safety level advertised for this operation. <b>This is metadata only:</b> setting
    /// <see cref="AgentSafetyLevel.Confirm"/> or <see cref="AgentSafetyLevel.Dangerous"/> here does not, by
    /// itself, prevent unconfirmed invocation. Enforcement only happens if the <see cref="OperationInvoker"/>
    /// that executes the operation is configured with a policy implementing
    /// <see cref="IConfirmationEnforcingPolicy"/> (for example <see cref="DangerousOperationConfirmationPolicy"/>).
    /// Without such a policy, <see cref="OperationInvoker.InvokeAsync"/> returns a failed result
    /// (carrying the <see cref="ConfirmationPolicyMissingException"/> message) for any operation whose <see cref="SafetyLevel"/> is
    /// above <see cref="AgentSafetyLevel.Safe"/>, so this trap is hard to miss at runtime.
    /// </summary>
    public AgentSafetyLevel SafetyLevel { get; init; } = AgentSafetyLevel.Safe;

    public string[] Examples { get; init; } = [];

    public string[] Aliases { get; init; } = [];

    public bool IsIdempotent { get; init; }

    private bool? _readOnly;
    private bool? _destructive;
    private bool? _idempotent;
    private bool? _openWorld;

    /// <summary>Gets the human-readable title advertised to hosts that support one (MCP <c>annotations.title</c>).</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Advertises whether the operation changes nothing (MCP <c>readOnlyHint</c>). Independent of <see cref="SafetyLevel"/>:
    /// when it is not set nothing is advertised.
    /// </summary>
    public bool ReadOnly { get => _readOnly ?? false; init => _readOnly = value; }

    /// <summary>Advertises whether the operation may perform destructive updates (MCP <c>destructiveHint</c>). Nothing is advertised when not set.</summary>
    public bool Destructive { get => _destructive ?? false; init => _destructive = value; }

    /// <summary>Advertises whether repeating the call with the same inputs has no further effect (MCP <c>idempotentHint</c>). Nothing is advertised when not set.</summary>
    public bool Idempotent { get => _idempotent ?? false; init => _idempotent = value; }

    /// <summary>Advertises whether the operation talks to an open world of external entities (MCP <c>openWorldHint</c>). Nothing is advertised when not set.</summary>
    public bool OpenWorld { get => _openWorld ?? false; init => _openWorld = value; }

    internal OperationHints GetHints() => new(Title, _readOnly, _destructive, _idempotent, _openWorld);
}

/// <summary>
/// Independent, optional descriptive hints about an operation. A <see langword="null"/> member is unset and is not advertised.
/// Hosts map them to their own vocabulary (the MCP adapter maps them to <c>ToolAnnotations</c>).
/// </summary>
public sealed record OperationHints(
    string? Title = null,
    bool? ReadOnly = null,
    bool? Destructive = null,
    bool? Idempotent = null,
    bool? OpenWorld = null)
{
    public static OperationHints Empty { get; } = new();

    /// <summary>Gets a value indicating whether no hint is set.</summary>
    public bool IsEmpty => Title is null && ReadOnly is null && Destructive is null && Idempotent is null && OpenWorld is null;

    /// <summary>Returns these hints with every unset member filled from <paramref name="fallback"/>.</summary>
    public OperationHints WithFallback(OperationHints fallback) => new(
        Title ?? fallback.Title,
        ReadOnly ?? fallback.ReadOnly,
        Destructive ?? fallback.Destructive,
        Idempotent ?? fallback.Idempotent,
        OpenWorld ?? fallback.OpenWorld);
}

/// <summary>
/// Describes how risky an operation is intended to be, for display and hinting purposes.
/// </summary>
/// <remarks>
/// This enum is descriptive metadata surfaced to callers (CLI help text, MCP tool descriptions, etc.). It is
/// <b>not</b> self-enforcing: nothing in the operation catalog or invocation pipeline automatically blocks
/// unconfirmed execution of a <see cref="Confirm"/> or <see cref="Dangerous"/> operation. Enforcement requires
/// pairing the operation with an <see cref="OperationInvoker"/> policy implementing
/// <see cref="IConfirmationEnforcingPolicy"/>, such as <see cref="DangerousOperationConfirmationPolicy"/>. If no
/// such policy is supplied, <see cref="OperationInvoker.InvokeAsync"/> returns a failed result
/// (carrying the <see cref="ConfirmationPolicyMissingException"/> message) rather than silently running the operation unconfirmed.
/// </remarks>
public enum AgentSafetyLevel
{
    /// <summary>The operation has no meaningful side effects requiring confirmation.</summary>
    Safe,

    /// <summary>
    /// The operation should require explicit confirmation before it runs.
    /// </summary>
    Confirm,

    /// <summary>
    /// The operation is destructive or otherwise high-risk and should require explicit confirmation before it runs.
    /// </summary>
    Dangerous
}
