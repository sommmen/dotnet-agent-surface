namespace DotNetAgentSurface.Core;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AgentOperationAttribute : Attribute
{
    public AgentOperationAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    public string? Category { get; init; }

    /// <summary>
    /// Gets the safety level advertised for this operation. <b>This is metadata only:</b> setting
    /// <see cref="AgentSafetyLevel.Confirm"/> or <see cref="AgentSafetyLevel.Dangerous"/> here does not, by
    /// itself, prevent unconfirmed invocation. Enforcement only happens if the <see cref="OperationInvoker"/>
    /// that executes the operation is configured with a policy implementing
    /// <see cref="IConfirmationEnforcingPolicy"/> (for example <see cref="DangerousOperationConfirmationPolicy"/>).
    /// Without such a policy, <see cref="OperationInvoker.InvokeAsync"/> throws
    /// <see cref="ConfirmationPolicyMissingException"/> for any operation whose <see cref="SafetyLevel"/> is
    /// above <see cref="AgentSafetyLevel.Safe"/>, so this trap is hard to miss at runtime.
    /// </summary>
    public AgentSafetyLevel SafetyLevel { get; init; } = AgentSafetyLevel.Safe;

    public string[] Examples { get; init; } = [];

    public string[] Aliases { get; init; } = [];

    public bool IsIdempotent { get; init; }
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
/// such policy is supplied, <see cref="OperationInvoker.InvokeAsync"/> throws
/// <see cref="ConfirmationPolicyMissingException"/> rather than silently running the operation unconfirmed.
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
